// Host 연결: 전송(LAN TLS / 인터넷 WebRTC) → hello/challenge/proof → 화면 수신.
// Windows Client의 RemoteHostConnection.cs와 같은 순서입니다.

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:async/async.dart';

import 'auth.dart';
import 'protocol.dart';

/// 처음 보는 Host에 연결할 때 사용자에게 보여 줄 정보
class NewHostInfo {
  NewHostInfo(this.hostId, this.hostName, this.fingerprint);
  final String hostId;
  final String hostName;
  final String fingerprint;
}

/// 사용자에게 그대로 보여 줘도 되는 실패 사유
class ConnectionFailed implements Exception {
  ConnectionFailed(this.message, [this.errorCode]);
  final String message;
  final String? errorCode;

  @override
  String toString() => message;
}

enum HostTrust { unknown, trusted, mismatch }

/// Host ID → 인증서 지문 저장소 (TOFU)
abstract class KnownHosts {
  Future<HostTrust> check(String hostId, String fingerprintHex);
  Future<void> save(String hostId, String fingerprintHex);
}

class DeviceCredential {
  DeviceCredential(this.deviceId, this.secret);
  final String deviceId;
  final Uint8List secret;
}

/// "이 기기 기억하기"로 받은 장치 자격 증명 저장소 (앱: 보안 저장소, 테스트: 메모리)
abstract class DeviceCredentials {
  Future<DeviceCredential?> get(String hostId);
  Future<void> save(String hostId, DeviceCredential credential);
  Future<void> remove(String hostId);
}

/// 로그인 정보. secret은 메모리에만 두고 저장하지 않습니다.
class LoginRequest {
  const LoginRequest({required this.method, required this.secret, this.rememberDevice = false});
  final String method; // AuthMethods.accessCode | AuthMethods.password
  final String secret;
  final bool rememberDevice;
}

/// 연결 중 사용자에게 물어볼 것들
abstract class ConnectPrompts {
  Future<bool> confirmNewHost(NewHostInfo info);

  /// 2단계 인증 6자리. 취소하면 null
  Future<String?> askTotp(String hostName);
}

/// 암호화된 연결 하나. 위의 인증·화면 처리는 전송 방식과 관계없이 같습니다.
abstract class ClientTransport {
  Stream<List<int>> get input;
  void add(List<int> bytes);
  Future<void> flush();
  void destroy();

  /// 인증 증명값에 섞는 값 (TLS: 인증서 해시, WebRTC: DTLS 지문 해시)
  Uint8List get channelBinding;

  /// LAN이면 TOFU로 확인할 인증서 해시, 아니면 null
  Uint8List? get pinnedFingerprint;

  String get kind;
}

class TlsTransport implements ClientTransport {
  TlsTransport(this._socket, this.channelBinding);
  final SecureSocket _socket;

  @override
  final Uint8List channelBinding;

  @override
  Uint8List? get pinnedFingerprint => channelBinding;

  @override
  String get kind => 'lan';

  @override
  Stream<List<int>> get input => _socket;

  @override
  void add(List<int> bytes) => _socket.add(bytes);

  @override
  Future<void> flush() => _socket.flush();

  @override
  void destroy() => _socket.destroy();

  /// LAN 직접 연결 (TCP + TLS)
  static Future<TlsTransport> connect(String host, int port, {Duration timeout = const Duration(seconds: 15)}) async {
    final SecureSocket socket;
    try {
      // Host는 자체 서명 인증서라 여기서는 받아 두고, TOFU 지문과 증명값으로 검증합니다.
      socket = await SecureSocket.connect(host, port, timeout: timeout, onBadCertificate: (_) => true);
    } on SocketException catch (e) {
      throw ConnectionFailed('$host:$port에 연결할 수 없습니다.\nHost 실행 여부, IP, 방화벽, 같은 Wi-Fi인지 확인하세요.\n(${e.osError?.message ?? e.message})');
    } on HandshakeException catch (e) {
      throw ConnectionFailed('TLS 연결 실패: ${e.message}');
    }
    socket.setOption(SocketOption.tcpNoDelay, true);
    final certificate = socket.peerCertificate;
    if (certificate == null) {
      socket.destroy();
      throw ConnectionFailed('Host 인증서를 받지 못했습니다.');
    }
    return TlsTransport(socket, sha256Bytes(certificate.der));
  }
}

class RemoteHostConnection {
  RemoteHostConnection._(this._transport, this._messages, this.hostId, this.hostName, this.screenWidth, this.screenHeight, this.method,
      this.monitors, this.features);

  final ClientTransport _transport;
  final StreamQueue<ReceivedMessage> _messages;
  final String hostId;
  final String hostName;
  final int screenWidth;
  final int screenHeight;
  final String method;

  /// Host 모니터 목록: [{index, x, y, width, height, primary}]
  final List<Map<String, dynamic>> monitors;

  /// Host가 허용한 기능: input, clipboard, file_transfer, audio, power, monitor_select
  final List<String> features;

  bool hasFeature(String feature) => features.contains(feature);

  final _frames = StreamController<VideoFrame>();
  final _controls = StreamController<Map<String, dynamic>>.broadcast();
  final _closed = Completer<String>();
  bool _closing = false;

  /// 받은 영상 프레임. 표시한 뒤 반드시 [ack]를 호출해야 다음 프레임이 옵니다.
  Stream<VideoFrame> get frames => _frames.stream;

  /// 받은 제어 메시지 (클립보드, 파일, 상태 등 - STEP 9)
  Stream<Map<String, dynamic>> get controls => _controls.stream;

  final _binaries = StreamController<ReceivedMessage>.broadcast();

  /// 받은 파일 조각 / 오디오
  Stream<ReceivedMessage> get binaries => _binaries.stream;

  /// 연결이 끊긴 이유. 사용자가 [close]한 경우에도 완료됩니다.
  Future<String> get closed => _closed.future;

  String get transportKind => _transport.kind;

  static Future<RemoteHostConnection> authenticate({
    required ClientTransport transport,
    required LoginRequest login,
    required String clientName,
    required String platform,
    required KnownHosts knownHosts,
    required DeviceCredentials devices,
    required ConnectPrompts prompts,
    Duration timeout = const Duration(seconds: 15),
  }) async {
    final decoder = FrameDecoder();
    final queue = StreamQueue(transport.input.expand(decoder.add));

    Future<Map<String, dynamic>> receiveControl(String expectedType, Duration wait) async {
      if (!await queue.hasNext.timeout(wait)) {
        throw ConnectionFailed('Host가 연결을 끊었습니다.');
      }
      final message = await queue.next;
      if (message.type != expectedType) {
        throw ProtocolException('$expectedType를 기대했지만 ${message.type ?? '다른 메시지'}를 받았습니다.');
      }
      return message.control!;
    }

    try {
      // 1) hello
      final clientNonce = createNonce();
      transport.add(encodeControl(helloMessage(clientName, platform, base64.encode(clientNonce))));

      // 2) challenge
      final challenge = await receiveControl('auth_challenge', timeout);
      final serverNonce = decodeNonce(challenge['server_nonce']);
      if (serverNonce == null) {
        throw ProtocolException('server_nonce 형식이 잘못되었습니다.');
      }
      final hostId = challenge['host_id'] as String? ?? '';
      final hostName = challenge['host_name'] as String? ?? '';
      final methods = (challenge['auth_methods'] as List?)?.cast<String>() ?? [AuthMethods.accessCode];

      // 3) LAN이면 인증서 지문 확인 (증명값을 보내기 전에)
      final pinned = transport.pinnedFingerprint;
      if (pinned != null) {
        switch (await knownHosts.check(hostId, hexUpper(pinned))) {
          case HostTrust.mismatch:
            throw ConnectionFailed('경고: $hostId의 인증서가 이전과 다릅니다.\n'
                '중간자 공격일 수 있어 연결을 중단했습니다.\n'
                'Host를 다시 설치한 것이 확실하면 PC 목록에서 이 PC를 삭제 후 다시 추가하세요.');
          case HostTrust.unknown:
            if (!await prompts.confirmNewHost(NewHostInfo(hostId, hostName, formatFingerprint(pinned)))) {
              throw ConnectionFailed('연결을 취소했습니다.');
            }
          case HostTrust.trusted:
            break;
        }
      }

      // 4) 인증 방식: 신뢰된 장치로 등록되어 있으면 우선 사용
      final saved = await devices.get(hostId);
      String method;
      Uint8List key;
      String? deviceId;
      String? totp;

      if (saved != null && methods.contains(AuthMethods.device)) {
        method = AuthMethods.device;
        key = saved.secret;
        deviceId = saved.deviceId;
      } else {
        if (saved != null) {
          // 저장된 자격 증명을 Host가 받지 않음 = 등록 해제됨
          await devices.remove(hostId);
          if (login.secret.isEmpty) {
            throw ConnectionFailed('이 기기의 신뢰된 장치 등록이 해제되었습니다. 비밀번호나 접속 코드로 다시 로그인하세요.',
                AuthErrorCodes.deviceRevoked);
          }
        }

        method = login.method;
        if (!methods.contains(method)) {
          throw ConnectionFailed(method == AuthMethods.password
              ? '이 PC에는 비밀번호가 설정되어 있지 않습니다. 접속 코드로 로그인하세요.'
              : '이 PC는 접속 코드 로그인을 사용하지 않습니다. 비밀번호로 로그인하세요.');
        }
        if (login.secret.isEmpty) {
          throw ConnectionFailed(method == AuthMethods.password ? '비밀번호를 입력하세요.' : '접속 코드를 입력하세요.');
        }

        if (method == AuthMethods.password) {
          final salt = decodeFixed(challenge['password_salt'], 16);
          final iterations = (challenge['password_iterations'] as num?)?.toInt() ?? 0;
          if (salt == null || iterations < 10000 || iterations > 5000000) {
            throw ProtocolException('비밀번호 매개변수가 잘못되었습니다.');
          }
          // PBKDF2는 수백 ms~수 초 걸리므로 별도 isolate에서 계산해 화면이 멈추지 않게 합니다.
          key = await _derivePasswordKeyInBackground(login.secret, salt, iterations);
        } else {
          key = deriveAccessCodeKey(login.secret);
        }

        if (challenge['totp_required'] == true) {
          totp = (await prompts.askTotp(hostName))?.trim();
          if (totp == null) {
            throw ConnectionFailed('2단계 인증을 취소했습니다.');
          }
        }
      }

      // 5) 증명값
      final proof = computeProof(AuthRole.client, key, clientNonce, serverNonce, transport.channelBinding);
      transport.add(encodeControl(authResponseMessage(
        base64.encode(proof),
        method: method,
        deviceId: deviceId,
        totp: totp,
        registerDevice: login.rememberDevice && method != AuthMethods.device,
        deviceName: clientName,
      )));

      // 6) 결과 (Host 승인 대기 포함 최대 60초) + Host 증명 확인
      final result = await receiveControl('auth_result', const Duration(seconds: 60));
      if (result['success'] != true) {
        final code = result['error_code'] as String?;
        if (code == AuthErrorCodes.deviceRevoked) {
          await devices.remove(hostId);
        }
        throw ConnectionFailed(result['error'] as String? ?? '인증에 실패했습니다.', code);
      }
      if (!verifyProof(AuthRole.host, key, clientNonce, serverNonce, transport.channelBinding, result['host_proof'] as String?)) {
        throw ConnectionFailed('Host가 올바른 증명을 보내지 않았습니다. 연결을 중단합니다.');
      }

      // 7) 저장
      if (pinned != null) {
        await knownHosts.save(hostId, hexUpper(pinned));
      }
      final newSecret = decodeFixed(result['device_secret'], keyBytes);
      final newDeviceId = result['device_id'] as String?;
      if (newDeviceId != null && newSecret != null) {
        await devices.save(hostId, DeviceCredential(newDeviceId, newSecret));
      }

      final connection = RemoteHostConnection._(
        transport,
        queue,
        hostId,
        hostName,
        (result['screen_width'] as num?)?.toInt() ?? 0,
        (result['screen_height'] as num?)?.toInt() ?? 0,
        method,
        ((result['monitors'] as List?) ?? []).cast<Map<String, dynamic>>(),
        ((result['features'] as List?) ?? []).cast<String>(),
      );
      unawaited(connection._receiveLoop());
      return connection;
    } on TimeoutException {
      _dispose(transport, queue);
      throw ConnectionFailed('응답 시간이 초과되었습니다.');
    } on ProtocolException catch (e) {
      _dispose(transport, queue);
      throw ConnectionFailed('프로토콜 오류: ${e.message}');
    } on SocketException catch (e) {
      _dispose(transport, queue);
      throw ConnectionFailed('연결이 끊겼습니다: ${e.message}');
    } catch (_) {
      _dispose(transport, queue);
      rethrow;
    }
  }

  static void _dispose(ClientTransport transport, StreamQueue<ReceivedMessage> queue) {
    unawaited(queue.cancel());
    transport.destroy();
  }

  Future<void> _receiveLoop() async {
    var reason = '연결이 종료되었습니다.';
    try {
      while (await _messages.hasNext) {
        final message = await _messages.next;
        if (message.video != null) {
          _frames.add(message.video!);
        } else if (message.type == 'bye') {
          reason = 'Host가 연결을 종료했습니다: ${message.control!['reason']}';
          break;
        } else if (message.control != null) {
          _controls.add(message.control!);
        } else if (message.binary != null) {
          _binaries.add(message);
        }
      }
    } catch (e) {
      reason = '연결이 끊겼습니다: $e';
    }

    _transport.destroy();
    await _frames.close();
    await _controls.close();
    await _binaries.close();
    if (!_closed.isCompleted) {
      _closed.complete(_closing ? '연결을 종료했습니다.' : reason);
    }
  }

  void send(Map<String, Object?> message) => sendRaw(encodeControl(message));

  void sendRaw(List<int> bytes) {
    if (_closing || _closed.isCompleted) {
      return;
    }
    try {
      _transport.add(bytes);
    } on StateError {
      // 이미 닫힘
    }
  }

  void ack(int frameId) => send(frameAckMessage(frameId));

  /// 보낸 데이터가 실제로 나갈 때까지 기다립니다 (큰 파일을 보낼 때 메모리가 쌓이지 않도록).
  Future<void> flush() async {
    try {
      await _transport.flush();
    } catch (_) {}
  }

  Future<void> close() async {
    if (_closing) {
      return;
    }
    send(byeMessage('client closed'));
    _closing = true;
    try {
      await _transport.flush().timeout(const Duration(milliseconds: 300));
    } catch (_) {}
    _transport.destroy();
    unawaited(_messages.cancel(immediate: true));
    if (!_closed.isCompleted) {
      _closed.complete('연결을 종료했습니다.');
    }
  }
}

/// 최상위 함수에서 isolate를 만들어야 클로저가 소켓 등 보낼 수 없는 객체를 잡지 않습니다.
Future<Uint8List> _derivePasswordKeyInBackground(String password, Uint8List salt, int iterations) =>
    Isolate.run(() => derivePasswordKey(password, salt, iterations));

/// 온라인 상태 확인: TCP 포트가 열려 있는지만 봅니다. (인증 정보는 보내지 않음)
Future<bool> probeHost(String host, int port, {Duration timeout = const Duration(milliseconds: 1500)}) async {
  try {
    final socket = await Socket.connect(host, port, timeout: timeout);
    socket.destroy();
    return true;
  } catch (_) {
    return false;
  }
}
