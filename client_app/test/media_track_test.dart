// STEP 14: 미디어 트랙 협상 (가짜 MediaPeer로 flutter_webrtc 없이 시험)

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/media/media_track.dart';
import 'package:remodesktop_client/protocol/auth.dart';
import 'package:remodesktop_client/protocol/connection.dart';
import 'package:remodesktop_client/screens/remote_screen.dart';

import 'support/fake_host.dart';

class FakeMediaPeer implements MediaPeer {
  FakeMediaPeer(this.iceServers);

  final List<Map<String, dynamic>> iceServers;
  final _changes = ValueNotifier<int>(0);
  void Function(String, String?, int?)? _onCandidate;
  String? answer;
  final List<String> candidates = [];
  bool disposed = false;
  bool? audio;
  Size? size;

  /// 로컬 ICE 후보가 생긴 것처럼
  void emitCandidate(String c) => _onCandidate?.call(c, '0', 0);

  void resize(Size s) {
    size = s;
    _changes.value++;
  }

  @override
  Future<String> createOffer() async => 'FAKE-OFFER';

  @override
  Future<void> setAnswer(String sdp) async => answer = sdp;

  @override
  Future<void> addCandidate(String candidate, String? sdpMid, int? sdpMLineIndex) async => candidates.add(candidate);

  @override
  set onCandidate(void Function(String candidate, String? sdpMid, int? sdpMLineIndex) callback) => _onCandidate = callback;

  @override
  Size? get videoSize => size;

  @override
  Listenable get changes => _changes;

  @override
  set audioEnabled(bool enabled) => audio = enabled;

  @override
  Widget buildView() => const ColoredBox(key: ValueKey('fake-video'), color: Colors.blue);

  @override
  Future<void> dispose() async => disposed = true;
}

void main() {
  group('MediaTrackClient', () {
    test('offer, ICE (early and late), answer, state', () async {
      final sent = <Map<String, Object?>>[];
      FakeMediaPeer? peer;
      final client = MediaTrackClient(
        send: sent.add,
        iceServers: [
          {'urls': ['turn:t.example:3478'], 'username': 'u', 'credential': 'c'}
        ],
        factory: (servers) async => peer = FakeMediaPeer(servers),
      );

      // peer가 만들어지기 전에 온 Host ICE 후보는 보관했다가 적용
      expect(await client.handle({'type': 'media_ice', 'candidate': 'early-1', 'sdp_mid': '0', 'sdp_mline_index': 0}), isTrue);
      await client.start();
      expect(peer!.iceServers.single['username'], 'u');
      expect(sent.single, {'type': 'media_offer', 'sdp': 'FAKE-OFFER'});
      expect(peer!.candidates, ['early-1']);

      peer!.emitCandidate('local-1');
      expect(sent.last['type'], 'media_ice');
      expect(sent.last['candidate'], 'local-1');

      await client.handle({'type': 'media_answer', 'sdp': 'FAKE-ANSWER'});
      await client.handle({'type': 'media_ice', 'candidate': 'late-1'});
      await client.handle({'type': 'media_ice', 'candidate': ''}); // 수집 끝 표시는 무시
      expect(peer!.answer, 'FAKE-ANSWER');
      expect(peer!.candidates, ['early-1', 'late-1']);

      expect(client.active.value, isFalse);
      await client.handle({'type': 'media_state', 'active': true});
      expect(client.active.value, isTrue);
      await client.handle({'type': 'media_state', 'active': false, 'error': 'ICE 실패'});
      expect(client.active.value, isFalse);
      expect(client.error, 'ICE 실패');

      expect(await client.handle({'type': 'stream_stats'}), isFalse);
      await client.dispose();
      expect(peer!.disposed, isTrue);
    });

    test('factory failure keeps JPEG path', () async {
      final sent = <Map<String, Object?>>[];
      final client = MediaTrackClient(send: sent.add, factory: (_) async => throw UnsupportedError('no webrtc'));
      await client.start();
      expect(sent, isEmpty);
      expect(client.error, contains('no webrtc'));
      expect(client.active.value, isFalse);
    });
  });

  testWidgets('RemoteScreen switches to the video track and back', (tester) async {
    debugDefaultTargetPlatformOverride = TargetPlatform.android;
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1;
    late FakeHost host;
    FakeMediaPeer? peer;

    final connection = await tester.runAsync(() async {
      host = FakeHost(features: ['input', 'audio', 'media_track']);
      await host.start();
      return connectTo('127.0.0.1', host.port, const LoginRequest(method: AuthMethods.accessCode, secret: 'testcode-42'));
    });

    await tester.pumpWidget(MaterialApp(
      home: RemoteScreen(connection: connection!, title: 'FAKE', mediaPeerFactory: (servers) async => peer = FakeMediaPeer(servers)),
    ));
    await tester.pump();

    Future<void> settle() async {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 150)));
      await tester.pump();
    }

    await settle();
    expect(host.received.where((m) => m['type'] == 'media_offer').single['sdp'], 'FAKE-OFFER');
    expect(find.byKey(const ValueKey('fake-video')), findsNothing);

    // Host: answer → 연결됨 → 영상 트랙 화면
    host.sendToClients({'type': 'media_answer', 'sdp': 'ANSWER'});
    host.sendToClients({'type': 'media_state', 'active': true});
    await settle();
    expect(peer!.answer, 'ANSWER');
    expect(find.byKey(const ValueKey('fake-video')), findsOneWidget);

    // 영상 크기가 바뀌면 제목에 반영 (적응형 화질로 해상도가 줄어든 경우 등)
    peer!.resize(const Size(1280, 720));
    await tester.pump();
    expect(find.textContaining('1280x720'), findsOneWidget);

    // 소리 켜기: Host에 audio start + 기기 쪽 트랙 켜기
    await tester.tap(find.text('메뉴'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(CheckedPopupMenuItem<String>, 'PC 소리 듣기'));
    await tester.pumpAndSettle();
    await settle();
    expect(host.received.any((m) => m['type'] == 'audio' && m['action'] == 'start'), isTrue);
    expect(peer!.audio, isTrue);

    // 미디어 연결이 끊기면 기본 화질(JPEG)로 돌아가고 안내
    host.sendToClients({'type': 'media_state', 'active': false, 'error': '미디어 연결 끊김'});
    await settle();
    expect(find.byKey(const ValueKey('fake-video')), findsNothing);
    expect(find.textContaining('기본 화질'), findsOneWidget);

    await tester.pumpWidget(const SizedBox());
    await tester.runAsync(() async {
      await Future<void>.delayed(const Duration(milliseconds: 100));
      await host.stop();
    });
    expect(peer!.disposed, isTrue);
    debugDefaultTargetPlatformOverride = null;
    tester.view.reset();
  });
}
