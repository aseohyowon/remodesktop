// 테스트용 가짜 Host: 실제 Windows Host와 같은 인증 절차(접속 코드/비밀번호/신뢰된 장치/TOTP 요구)를
// Dart로 흉내 내고, 앱이 보낸 제어 메시지를 기록합니다. (실제 PC에 클릭/키 입력이 일어나지 않음)

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:async/async.dart';
import 'package:remodesktop_client/protocol/auth.dart';
import 'package:remodesktop_client/protocol/connection.dart';
import 'package:remodesktop_client/protocol/protocol.dart';

class FakeHost {
  FakeHost({
    this.accessCode = 'TESTCODE42',
    this.password,
    this.totpCode,
    this.screenWidth = 1920,
    this.screenHeight = 1080,
    this.features,
  });

  final String accessCode;
  final String? password;

  /// null이 아니면 2단계 인증 요구, 이 값이 맞는 코드
  final String? totpCode;
  final int screenWidth;
  final int screenHeight;

  /// auth_result.features (null이면 보내지 않음)
  final List<String>? features;

  static const passwordIterations = 10000;
  final Uint8List _salt = Uint8List.fromList(List.generate(16, (i) => i * 7));

  /// 등록된 장치: id → secret
  final Map<String, Uint8List> devices = {};

  late SecureServerSocket _server;
  late List<int> certificateHash;
  final List<SecureSocket> _clients = [];

  /// 인증 이후 받은 제어 메시지 (frame_ack 제외)
  final List<Map<String, dynamic>> received = [];

  /// 마지막 auth_response
  Map<String, dynamic>? lastAuthResponse;

  int get port => _server.port;

  Future<void> start() async {
    final certPem = File('test/fixtures/fake_host_cert.pem').readAsStringSync();
    final keyPem = File('test/fixtures/fake_host_key.pem').readAsStringSync();
    certificateHash = sha256Bytes(base64.decode(certPem.split('\n').where((l) => l.isNotEmpty && !l.startsWith('-----')).join()));

    final context = SecurityContext()
      ..useCertificateChainBytes(utf8.encode(certPem))
      ..usePrivateKeyBytes(utf8.encode(keyPem));
    _server = await SecureServerSocket.bind(InternetAddress.loopbackIPv4, 0, context);
    _server.listen(_handle);
  }

  Future<void> _handle(SecureSocket socket) async {
    try {
      await _handleInner(socket);
    } catch (_) {
      // Client가 인증 도중 끊은 경우
    }
  }

  Future<void> _handleInner(SecureSocket socket) async {
    _clients.add(socket);
    final decoder = FrameDecoder();
    final queue = StreamQueue(socket.expand(decoder.add));

    final hello = (await queue.next).control!;
    final clientNonce = decodeNonce(hello['client_nonce'])!;
    final serverNonce = createNonce();
    socket.add(encodeControl({
      'type': 'auth_challenge',
      'protocol_version': protocolVersion,
      'host_id': 'HOST-FAKE01',
      'host_name': 'FAKE-PC',
      'server_nonce': base64.encode(serverNonce),
      'auth_methods': [
        AuthMethods.accessCode,
        if (password != null) AuthMethods.password,
        if (devices.isNotEmpty) AuthMethods.device,
      ],
      if (password != null) 'password_salt': base64.encode(_salt),
      if (password != null) 'password_iterations': passwordIterations,
      'totp_required': totpCode != null,
    }));

    final response = (await queue.next).control!;
    lastAuthResponse = response;
    final method = response['method'] as String? ?? AuthMethods.accessCode;
    final List<int>? key = switch (method) {
      AuthMethods.accessCode => deriveAccessCodeKey(accessCode),
      AuthMethods.password when password != null => derivePasswordKey(password!, _salt, passwordIterations),
      AuthMethods.device => devices[response['device_id']],
      _ => null,
    };

    String? error;
    String? errorCode;
    if (key == null) {
      error = '장치 인증 실패';
      errorCode = AuthErrorCodes.deviceRevoked;
    } else if (!verifyProof(AuthRole.client, key, clientNonce, serverNonce, certificateHash, response['proof'] as String?)) {
      error = '인증 실패';
      errorCode = method == AuthMethods.device ? AuthErrorCodes.deviceRevoked : AuthErrorCodes.invalidCredentials;
    } else if (method != AuthMethods.device && totpCode != null && response['totp'] != totpCode) {
      error = '2단계 인증 코드가 올바르지 않습니다.';
      errorCode = AuthErrorCodes.totpRequired;
    }

    if (error != null) {
      socket.add(encodeControl({'type': 'auth_result', 'success': false, 'error': error, 'error_code': errorCode, 'screen_width': 0, 'screen_height': 0}));
      await socket.close();
      return;
    }

    String? newDeviceId;
    Uint8List? newSecret;
    if (response['register_device'] == true && method != AuthMethods.device) {
      newDeviceId = 'DEV-${devices.length + 1}';
      newSecret = randomBytes(keyBytes);
      devices[newDeviceId] = newSecret;
    }

    socket.add(encodeControl({
      'type': 'auth_result',
      'success': true,
      'host_proof': base64.encode(computeProof(AuthRole.host, key!, clientNonce, serverNonce, certificateHash)),
      'screen_width': screenWidth,
      'screen_height': screenHeight,
      'features': ?features,
      'device_id': ?newDeviceId,
      if (newSecret != null) 'device_secret': base64.encode(newSecret),
    }));

    try {
      while (await queue.hasNext) {
        final message = await queue.next;
        if (message.control != null && message.type != 'frame_ack') {
          received.add(message.control!);
        }
      }
    } catch (_) {}
  }

  /// 앱 쪽 테스트에서 보내는 메시지 (예: 클립보드)
  void sendToClients(Map<String, Object?> message) {
    for (final c in _clients) {
      c.add(encodeControl(message));
    }
  }

  Future<void> stop() async {
    for (final c in _clients) {
      c.destroy();
    }
    await _server.close();
  }
}

class MemoryKnownHosts implements KnownHosts {
  final map = <String, String>{};

  @override
  Future<HostTrust> check(String hostId, String fingerprintHex) async {
    final saved = map[hostId];
    if (saved == null) return HostTrust.unknown;
    return saved == fingerprintHex ? HostTrust.trusted : HostTrust.mismatch;
  }

  @override
  Future<void> save(String hostId, String fingerprintHex) async => map[hostId] = fingerprintHex;
}

class MemoryDevices implements DeviceCredentials {
  final map = <String, DeviceCredential>{};

  @override
  Future<DeviceCredential?> get(String hostId) async => map[hostId];

  @override
  Future<void> remove(String hostId) async => map.remove(hostId);

  @override
  Future<void> save(String hostId, DeviceCredential credential) async => map[hostId] = credential;
}

class TestPrompts implements ConnectPrompts {
  TestPrompts({this.totp, this.trustNewHost = true});
  final String? totp;
  final bool trustNewHost;
  int newHostPrompts = 0;
  int totpPrompts = 0;

  @override
  Future<bool> confirmNewHost(NewHostInfo info) async {
    newHostPrompts++;
    return trustNewHost;
  }

  @override
  Future<String?> askTotp(String hostName) async {
    totpPrompts++;
    return totp;
  }
}

/// FakeHost(또는 실제 Host)에 LAN TLS로 연결 + 인증
Future<RemoteHostConnection> connectTo(
  String host,
  int port,
  LoginRequest login, {
  KnownHosts? knownHosts,
  DeviceCredentials? devices,
  ConnectPrompts? prompts,
}) async {
  final transport = await TlsTransport.connect(host, port);
  return RemoteHostConnection.authenticate(
    transport: transport,
    login: login,
    clientName: 'dart-test',
    platform: 'test',
    knownHosts: knownHosts ?? MemoryKnownHosts(),
    devices: devices ?? MemoryDevices(),
    prompts: prompts ?? TestPrompts(),
  );
}
