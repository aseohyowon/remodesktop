// STEP 14: flutter_webrtc로 만든 실제 미디어 수신기 (Android, iOS, macOS)

import 'package:flutter/widgets.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import 'media_track.dart';

class WebRtcMediaPeer implements MediaPeer {
  WebRtcMediaPeer._(this._pc, this._renderer);

  final RTCPeerConnection _pc;
  final RTCVideoRenderer _renderer;
  final _changes = ValueNotifier<int>(0); // 영상 크기/스트림이 바뀔 때마다 1씩 증가
  MediaStream? _stream;
  void Function(String, String?, int?)? _onCandidate;

  static Future<WebRtcMediaPeer> create(List<Map<String, dynamic>> iceServers) async {
    final renderer = RTCVideoRenderer();
    await renderer.initialize();
    final pc = await createPeerConnection({
      'sdpSemantics': 'unified-plan',
      'iceServers': [
        for (final s in iceServers)
          {
            'urls': (s['urls'] as List).cast<String>(),
            if (s['username'] != null) 'username': s['username'],
            if (s['credential'] != null) 'credential': s['credential'],
          }
      ],
    });
    final peer = WebRtcMediaPeer._(pc, renderer);
    pc.onIceCandidate = (candidate) {
      final c = candidate.candidate;
      if (c != null && c.isNotEmpty) peer._onCandidate?.call(c, candidate.sdpMid, candidate.sdpMLineIndex);
    };
    pc.onTrack = peer._onTrack;
    renderer.onResize = () => peer._changes.value++;
    return peer;
  }

  Future<void> _onTrack(RTCTrackEvent event) async {
    // 영상과 소리가 같은 스트림으로 오면 그대로, 아니면 하나로 모아 렌더러에 연결 (소리는 자동 재생)
    var stream = _stream;
    if (event.streams.isNotEmpty) {
      stream = event.streams.first;
    } else {
      stream ??= await createLocalMediaStream('remote');
      await stream.addTrack(event.track);
    }
    _stream = stream;
    if (event.track.kind == 'video') {
      _renderer.srcObject = stream;
      _changes.value++;
    }
  }

  @override
  Future<String> createOffer() async {
    await _pc.addTransceiver(
      kind: RTCRtpMediaType.RTCRtpMediaTypeVideo,
      init: RTCRtpTransceiverInit(direction: TransceiverDirection.RecvOnly),
    );
    await _pc.addTransceiver(
      kind: RTCRtpMediaType.RTCRtpMediaTypeAudio,
      init: RTCRtpTransceiverInit(direction: TransceiverDirection.RecvOnly),
    );
    final offer = await _pc.createOffer({});
    await _pc.setLocalDescription(offer);
    return offer.sdp!;
  }

  @override
  Future<void> setAnswer(String sdp) => _pc.setRemoteDescription(RTCSessionDescription(sdp, 'answer'));

  @override
  Future<void> addCandidate(String candidate, String? sdpMid, int? sdpMLineIndex) =>
      _pc.addCandidate(RTCIceCandidate(candidate, sdpMid, sdpMLineIndex));

  @override
  set onCandidate(void Function(String candidate, String? sdpMid, int? sdpMLineIndex) callback) => _onCandidate = callback;

  @override
  Size? get videoSize {
    final w = _renderer.videoWidth;
    final h = _renderer.videoHeight;
    return w > 0 && h > 0 ? Size(w.toDouble(), h.toDouble()) : null;
  }

  @override
  Listenable get changes => _changes;

  @override
  set audioEnabled(bool enabled) {
    for (final track in _stream?.getAudioTracks() ?? const <MediaStreamTrack>[]) {
      track.enabled = enabled;
    }
  }

  @override
  Widget buildView() => RTCVideoView(_renderer, objectFit: RTCVideoViewObjectFit.RTCVideoViewObjectFitContain);

  @override
  Future<void> dispose() async {
    _renderer.srcObject = null;
    await _renderer.dispose();
    await _pc.close();
    _changes.dispose();
  }
}
