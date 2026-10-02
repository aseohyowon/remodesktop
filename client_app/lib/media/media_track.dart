// STEP 14: WebRTC 미디어 트랙으로 화면(H.264)·소리(Opus) 받기
//
// Dart에는 H.264 디코더가 없어서, 지금까지 모바일·맥 앱은 JPEG 프레임만 받았습니다(대역폭이 크고 느림).
// Host가 'media_track' 기능을 알려 주면, 이미 인증된 연결 안에서 SDP를 주고받아 WebRTC 미디어 연결을 하나 더 만들고
// flutter_webrtc(libwebrtc)의 하드웨어 디코더(Android MediaCodec, iOS/macOS VideoToolbox)로 화면을 보여 줍니다.
// 소리도 같은 연결의 Opus 오디오 트랙으로 와서 기기 스피커로 재생됩니다.
//
//   앱 ──media_offer(recvonly 영상+소리)──► Host
//   앱 ◄─media_answer(H.264 + Opus)──────── Host
//   media_ice 양방향, 연결되면 Host가 media_state(active=true) → 이때부터 JPEG 프레임 대신 영상 트랙
//   실패하면 media_state(active=false) → JPEG 프레임으로 계속 (화면은 항상 보임)

import 'dart:async';

import 'package:flutter/widgets.dart';

import '../protocol/protocol.dart';

/// 미디어 연결 한쪽 (실제: flutter_webrtc, 테스트: 가짜)
abstract class MediaPeer {
  /// 받기 전용 영상/소리 트랜시버를 만들고 offer SDP를 돌려줍니다.
  Future<String> createOffer();

  Future<void> setAnswer(String sdp);

  Future<void> addCandidate(String candidate, String? sdpMid, int? sdpMLineIndex);

  /// 로컬 ICE 후보가 생길 때마다
  set onCandidate(void Function(String candidate, String? sdpMid, int? sdpMLineIndex) callback);

  /// 받은 영상 크기 (아직 없으면 null). 바뀌면 [changes]가 알립니다.
  Size? get videoSize;

  Listenable get changes;

  /// 소리 켜기/끄기 (기기에서만, Host 캡처는 audio 메시지로 따로 켬)
  set audioEnabled(bool enabled);

  /// 영상을 그리는 위젯 (주어진 영역을 채움)
  Widget buildView();

  Future<void> dispose();
}

typedef MediaPeerFactory = Future<MediaPeer> Function(List<Map<String, dynamic>> iceServers);

class MediaTrackClient {
  MediaTrackClient({required this.send, required this.factory, this.iceServers = const []});

  final void Function(Map<String, Object?>) send;
  final MediaPeerFactory factory;
  final List<Map<String, dynamic>> iceServers;

  MediaPeer? _peer;
  final List<Map<String, dynamic>> _early = []; // peer가 준비되기 전에 온 ICE
  bool _disposed = false;

  /// true면 화면이 영상 트랙으로 오는 중 (JPEG 프레임은 오지 않음)
  final ValueNotifier<bool> active = ValueNotifier(false);

  /// 미디어 연결을 쓸 수 없는 이유 (JPEG로 계속)
  String? error;

  MediaPeer? get peer => _peer;

  Future<void> start() async {
    try {
      final peer = await factory(iceServers);
      if (_disposed) {
        await peer.dispose();
        return;
      }
      _peer = peer;
      peer.onCandidate = (candidate, mid, index) => send(mediaIceMessage(candidate, mid, index));
      final offer = await peer.createOffer();
      send(mediaOfferMessage(offer));
      for (final ice in _early) {
        await _addCandidate(ice);
      }
      _early.clear();
    } catch (e) {
      error = '미디어 연결을 만들 수 없습니다: $e';
    }
  }

  /// media_* 메시지면 처리하고 true
  Future<bool> handle(Map<String, dynamic> message) async {
    switch (message['type']) {
      case 'media_answer':
        try {
          await _peer?.setAnswer(message['sdp'] as String);
        } catch (e) {
          error = 'answer 적용 실패: $e';
        }
        return true;
      case 'media_ice':
        if (_peer == null) {
          _early.add(message);
        } else {
          await _addCandidate(message);
        }
        return true;
      case 'media_state':
        final isActive = message['active'] == true;
        if (!isActive) error = message['error'] as String? ?? '미디어 연결이 끊겼습니다.';
        active.value = isActive && !_disposed;
        return true;
    }
    return false;
  }

  Future<void> _addCandidate(Map<String, dynamic> ice) async {
    final candidate = ice['candidate'] as String? ?? '';
    if (candidate.isEmpty) return;
    try {
      await _peer?.addCandidate(candidate, ice['sdp_mid'] as String?, (ice['sdp_mline_index'] as num?)?.toInt());
    } catch (_) {
      // 잘못된 후보 하나는 무시 (다른 후보로 연결될 수 있음)
    }
  }

  Future<void> dispose() async {
    _disposed = true;
    active.value = false;
    await _peer?.dispose();
    _peer = null;
    active.dispose();
  }
}
