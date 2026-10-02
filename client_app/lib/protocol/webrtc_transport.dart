// 인터넷 연결 (STEP 7): 시그널링 서버 → WebRTC Data Channel
// Windows의 WebRtcPeers.cs(Client 쪽)와 같은 순서입니다.
//
// 1) WebSocket으로 시그널링 서버에 connect(host_id)
// 2) connecting(session_id, ice_servers) → RTCPeerConnection 생성
// 3) Host의 offer를 받아 answer 전송, ICE 후보 교환
// 4) Data Channel이 열리면 LAN과 같은 인증·화면 프로토콜을 그 위에서 실행

import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:async/async.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

import 'connection.dart';

const _dataChannelLabel = 'remodesktop';
const _chunkSize = 16 * 1024;

/// SDP에서 DTLS 지문 추출 (C# WebRtcBinding.ExtractFingerprint와 동일)
String? extractFingerprint(String sdp) {
  for (final raw in sdp.split('\n')) {
    final line = raw.trim();
    if (line.toLowerCase().startsWith('a=fingerprint:sha-256 ')) {
      return line.substring('a=fingerprint:sha-256 '.length).trim().toUpperCase();
    }
  }
  return null;
}

/// channel binding = SHA-256("remodesktop-webrtc-v1|" + Host(offer) 지문 + "|" + Client(answer) 지문)
Uint8List webRtcChannelBinding(String hostOfferSdp, String clientAnswerSdp) {
  final host = extractFingerprint(hostOfferSdp);
  final client = extractFingerprint(clientAnswerSdp);
  if (host == null || client == null) {
    throw ConnectionFailed('WebRTC SDP에 DTLS 지문이 없습니다.');
  }
  return Uint8List.fromList(sha256.convert(ascii.encode('remodesktop-webrtc-v1|$host|$client')).bytes);
}

/// 시그널링 서버 주소(ws/wss)로 상태 조회 HTTP 주소를 만듭니다. wss://x/ws → https://x/api/status
Uri statusUri(Uri signaling, List<String> hostIds) => signaling.replace(
      scheme: signaling.scheme == 'wss' ? 'https' : 'http',
      path: '/api/status',
      queryParameters: {'ids': hostIds.join(',')},
    );

class WebRtcTransport implements ClientTransport {
  WebRtcTransport._(this._pc, this._channel, this._incoming, this.channelBinding);

  final RTCPeerConnection _pc;
  final RTCDataChannel _channel;
  final StreamController<List<int>> _incoming;

  @override
  final Uint8List channelBinding;

  @override
  Uint8List? get pinnedFingerprint => null; // 인터넷 연결은 TOFU 대신 DTLS 지문 바인딩

  @override
  String get kind => 'webrtc';

  @override
  Stream<List<int>> get input => _incoming.stream;

  @override
  void add(List<int> bytes) {
    for (var offset = 0; offset < bytes.length; offset += _chunkSize) {
      final end = offset + _chunkSize < bytes.length ? offset + _chunkSize : bytes.length;
      _channel.send(RTCDataChannelMessage.fromBinary(Uint8List.fromList(bytes.sublist(offset, end))));
    }
  }

  /// 송신 버퍼가 4 MB 아래로 내려갈 때까지 기다립니다.
  @override
  Future<void> flush() async {
    for (var i = 0; i < 500 && (_channel.bufferedAmount ?? 0) > 4 * 1024 * 1024; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }
  }

  @override
  void destroy() {
    unawaited(_channel.close().catchError((_) {}));
    unawaited(_pc.close().catchError((_) {}));
    if (!_incoming.isClosed) unawaited(_incoming.close());
  }

  static Future<WebRtcTransport> connect(Uri signaling, String hostId, {Duration timeout = const Duration(seconds: 30)}) async {
    final WebSocketChannel ws;
    try {
      ws = WebSocketChannel.connect(signaling);
      await ws.ready.timeout(const Duration(seconds: 15));
    } catch (e) {
      throw ConnectionFailed('시그널링 서버(${signaling.host})에 연결할 수 없습니다.\n$e');
    }

    final queue = StreamQueue(ws.stream);
    final opened = Completer<RTCDataChannel>();
    final incoming = StreamController<List<int>>();
    RTCPeerConnection? pc;
    String? sessionId;
    String? offerSdp;
    String? answerSdp;
    var peerConnected = false;

    void send(Map<String, Object?> message) => ws.sink.add(jsonEncode(message));

    void fail(Object error) {
      if (!opened.isCompleted) opened.completeError(error);
    }

    void attach(RTCDataChannel channel) {
      if (channel.label != _dataChannelLabel) return;
      // 채널을 받자마자 메시지 수신을 연결해 첫 메시지를 놓치지 않습니다.
      channel.onMessage = (message) {
        if (message.isBinary && !incoming.isClosed) incoming.add(message.binary);
      };
      channel.onDataChannelState = (state) {
        if (state == RTCDataChannelState.RTCDataChannelOpen && !opened.isCompleted) {
          opened.complete(channel);
        } else if (state == RTCDataChannelState.RTCDataChannelClosed && !incoming.isClosed) {
          incoming.close();
        }
      };
      if (channel.state == RTCDataChannelState.RTCDataChannelOpen && !opened.isCompleted) {
        opened.complete(channel);
      }
    }

    Future<void> handle(Map<String, dynamic> m) async {
      switch (m['type']) {
        case 'error':
          fail(ConnectionFailed(m['message'] as String? ?? '시그널링 오류'));
        case 'connecting':
          sessionId = m['session_id'] as String;
          final servers = (m['ice_servers'] as List? ?? []).cast<Map<String, dynamic>>();
          pc = await createPeerConnection({
            'iceServers': [
              for (final s in servers)
                {
                  'urls': (s['urls'] as List).cast<String>(),
                  if (s['username'] != null) 'username': s['username'],
                  if (s['credential'] != null) 'credential': s['credential'],
                }
            ],
          });
          pc!.onIceCandidate = (candidate) {
            if (candidate.candidate == null || candidate.candidate!.isEmpty) return;
            send({
              'type': 'ice',
              'session_id': sessionId,
              'candidate': candidate.candidate,
              'sdp_mid': candidate.sdpMid,
              'sdp_mline_index': candidate.sdpMLineIndex,
            });
          };
          pc!.onConnectionState = (state) {
            if (state == RTCPeerConnectionState.RTCPeerConnectionStateConnected) peerConnected = true;
            if (state == RTCPeerConnectionState.RTCPeerConnectionStateFailed) {
              fail(ConnectionFailed('WebRTC 연결 실패. 방화벽/NAT 환경이면 TURN 서버가 필요할 수 있습니다.'));
            }
          };
          pc!.onDataChannel = attach;
        case 'sdp' when m['sdp_type'] == 'offer' && pc != null:
          offerSdp = m['sdp'] as String;
          await pc!.setRemoteDescription(RTCSessionDescription(offerSdp, 'offer'));
          final answer = await pc!.createAnswer({});
          await pc!.setLocalDescription(answer);
          answerSdp = answer.sdp;
          send({'type': 'sdp', 'session_id': sessionId, 'sdp_type': 'answer', 'sdp': answerSdp});
        case 'ice' when pc != null:
          await pc!.addCandidate(RTCIceCandidate(
            m['candidate'] as String,
            m['sdp_mid'] as String?,
            (m['sdp_mline_index'] as num?)?.toInt(),
          ));
        case 'session_end':
          if (!peerConnected) fail(ConnectionFailed('Host가 연결을 거부했거나 취소했습니다.'));
      }
    }

    // 시그널링 메시지는 순서대로 하나씩 처리합니다.
    unawaited(() async {
      try {
        while (!opened.isCompleted && await queue.hasNext) {
          final raw = await queue.next;
          if (raw is String) {
            await handle(jsonDecode(raw) as Map<String, dynamic>);
          }
        }
        fail(ConnectionFailed('시그널링 서버 연결이 끊겼습니다.'));
      } catch (e) {
        fail(e is ConnectionFailed ? e : ConnectionFailed('인터넷 연결 실패: $e'));
      }
    }());

    send({'type': 'connect', 'host_id': hostId.trim().toUpperCase()});

    try {
      final channel = await opened.future.timeout(timeout);
      final binding = webRtcChannelBinding(offerSdp!, answerSdp!);
      // 화면 데이터는 P2P로 흐르므로 시그널링 연결은 닫습니다.
      unawaited(queue.cancel(immediate: true));
      unawaited(ws.sink.close());
      return WebRtcTransport._(pc!, channel, incoming, binding);
    } on TimeoutException {
      _cleanup(ws, queue, pc, incoming);
      throw ConnectionFailed('연결 시간이 초과되었습니다. 방화벽/NAT 환경이면 TURN 서버 설정이 필요할 수 있습니다.');
    } catch (_) {
      _cleanup(ws, queue, pc, incoming);
      rethrow;
    }
  }

  static void _cleanup(WebSocketChannel ws, StreamQueue<dynamic> queue, RTCPeerConnection? pc, StreamController<List<int>> incoming) {
    unawaited(queue.cancel(immediate: true));
    unawaited(ws.sink.close());
    unawaited(pc?.close());
    if (!incoming.isClosed) unawaited(incoming.close());
  }
}
