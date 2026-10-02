// RemoteScreen 위젯 테스트: 실제 화면 위젯에 터치/키보드 입력을 주고
// 가짜 Host(FakeHost)가 받은 프로토콜 메시지를 확인합니다.

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/protocol/auth.dart';
import 'package:remodesktop_client/protocol/connection.dart';
import 'package:remodesktop_client/screens/remote_screen.dart';

import 'support/fake_host.dart';

void main() {
  late FakeHost host;

  Future<void> openRemoteScreen(WidgetTester tester) async {
    debugDefaultTargetPlatformOverride = TargetPlatform.android;
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1;

    final connection = await tester.runAsync(() async {
      host = FakeHost();
      await host.start();
      return connectTo('127.0.0.1', host.port,
          const LoginRequest(method: AuthMethods.accessCode, secret: 'testcode-42'));
    });

    await tester.pumpWidget(MaterialApp(home: RemoteScreen(connection: connection!, title: 'FAKE')));
    await tester.pump();
  }

  /// 앱이 보낸 메시지가 가짜 Host에 도착할 때까지 잠깐 기다린 뒤 가져옵니다.
  Future<List<Map<String, dynamic>>> drain(WidgetTester tester) async {
    await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 150)));
    await tester.pump();
    final messages = List<Map<String, dynamic>>.from(host.received);
    host.received.clear();
    return messages;
  }

  List<String> summary(List<Map<String, dynamic>> messages) => messages.map((m) {
        return switch (m['type']) {
          'mouse_button' => '${m['button']}-${m['action']}',
          'key_down' => 'down:${m['code']}',
          'key_up' => 'up:${m['code']}',
          'text_input' => 'text:${m['text']}',
          'mouse_wheel' => 'wheel',
          _ => m['type'] as String,
        };
      }).toList();

  Future<void> close(WidgetTester tester) async {
    await tester.pumpWidget(const SizedBox());
    await tester.runAsync(() async {
      await Future<void>.delayed(const Duration(milliseconds: 100));
      await host.stop();
    });
    debugDefaultTargetPlatformOverride = null;
    tester.view.reset();
  }

  testWidgets('touchpad mode gestures → mouse messages', (tester) async {
    await openRemoteScreen(tester);
    final viewport = find.byKey(const ValueKey('remote-viewport'));
    final center = tester.getCenter(viewport);

    // 탭 → 커서 위치(처음엔 화면 중앙)에서 왼쪽 클릭
    var g = await tester.startGesture(center, kind: PointerDeviceKind.touch);
    await g.up();
    var m = await drain(tester);
    expect(summary(m), ['mouse_move', 'left-down', 'left-up']);
    expect(m.first['x'], closeTo(0.5, 0.001));

    // 한 손가락 이동 → 커서가 오른쪽으로 (클릭 없음)
    g = await tester.startGesture(center, kind: PointerDeviceKind.touch);
    for (var i = 0; i < 5; i++) {
      await g.moveBy(const Offset(20, 0));
    }
    await g.up();
    m = await drain(tester);
    expect(m.every((e) => e['type'] == 'mouse_move'), isTrue);
    expect(m.last['x'] as double, greaterThan(0.5));
    expect(m.last['y'], closeTo(0.5, 0.001));

    // 두 손가락 탭 → 우클릭
    final a = await tester.startGesture(center - const Offset(40, 0), kind: PointerDeviceKind.touch, pointer: 11);
    final b = await tester.startGesture(center + const Offset(40, 0), kind: PointerDeviceKind.touch, pointer: 12);
    await a.up();
    await b.up();
    expect(summary(await drain(tester)), ['mouse_move', 'right-down', 'right-up']);

    // 두 손가락을 위로 → 휠 (아래로 스크롤 = 음수)
    final c = await tester.startGesture(center - const Offset(40, 0), kind: PointerDeviceKind.touch, pointer: 21);
    final d = await tester.startGesture(center + const Offset(40, 0), kind: PointerDeviceKind.touch, pointer: 22);
    for (var i = 0; i < 6; i++) {
      await c.moveBy(const Offset(0, -15));
      await d.moveBy(const Offset(0, -15));
    }
    await c.up();
    await d.up();
    m = await drain(tester);
    expect(m, isNotEmpty);
    expect(m.every((e) => e['type'] == 'mouse_wheel'), isTrue);
    expect(m.fold<int>(0, (sum, e) => sum + (e['delta_y'] as int)), lessThan(0));

    // 길게 누른 뒤 이동 → 드래그 (left down → move → left up)
    g = await tester.startGesture(center, kind: PointerDeviceKind.touch);
    await tester.pump(const Duration(milliseconds: 600));
    await g.moveBy(const Offset(30, 30));
    await g.up();
    final s = summary(await drain(tester));
    expect(s.first, 'mouse_move');
    expect(s[1], 'left-down');
    expect(s.last, 'left-up');
    expect(s.where((e) => e == 'mouse_move').length, greaterThanOrEqualTo(2));

    // 핀치 → 확대만, 입력 메시지 없음
    final e = await tester.startGesture(center - const Offset(30, 0), kind: PointerDeviceKind.touch, pointer: 31);
    final f = await tester.startGesture(center + const Offset(30, 0), kind: PointerDeviceKind.touch, pointer: 32);
    await e.moveBy(const Offset(-80, 0));
    await f.moveBy(const Offset(80, 0));
    await e.up();
    await f.up();
    expect(await drain(tester), isEmpty);

    await close(tester);
  });

  testWidgets('direct touch mode: tap clicks where touched', (tester) async {
    await openRemoteScreen(tester);
    await tester.tap(find.text('터치패드'));
    await tester.pump();
    expect(find.text('직접 터치'), findsOneWidget);

    final rect = tester.getRect(find.byKey(const ValueKey('remote-viewport')));
    // 16:9 화면이 뷰포트 안에 맞춰지므로, 뷰포트 세로 중앙의 왼쪽 1/4 지점을 누름
    final point = Offset(rect.center.dx - rect.width / 4, rect.center.dy);
    final g = await tester.startGesture(point, kind: PointerDeviceKind.touch);
    await g.up();
    final m = await drain(tester);
    expect(summary(m), ['mouse_move', 'left-down', 'left-up']);
    expect(m.first['x'] as double, lessThan(0.4));
    expect(m.first['y'], closeTo(0.5, 0.02));

    await close(tester);
  });

  testWidgets('soft keyboard: text, Korean composition, backspace, enter, Ctrl+C', (tester) async {
    await openRemoteScreen(tester);
    await tester.tap(find.text('키보드'));
    await tester.pump();
    final field = find.byKey(const ValueKey('soft-keyboard'));
    await tester.showKeyboard(field);

    // 영문 입력
    tester.testTextInput.updateEditingValue(const TextEditingValue(text: ' hello', selection: TextSelection.collapsed(offset: 6)));
    await tester.pump();
    expect(summary(await drain(tester)), ['text:hello']);

    // 한글 조합 중에는 보내지 않고, 조합이 끝나면 완성 글자만 보냄
    tester.testTextInput.updateEditingValue(const TextEditingValue(
        text: ' 하', selection: TextSelection.collapsed(offset: 2), composing: TextRange(start: 1, end: 2)));
    await tester.pump();
    expect(await drain(tester), isEmpty);
    expect(find.text('입력 중: 하'), findsOneWidget);
    tester.testTextInput.updateEditingValue(const TextEditingValue(text: ' 한', selection: TextSelection.collapsed(offset: 2)));
    await tester.pump();
    expect(summary(await drain(tester)), ['text:한']);

    // Backspace (sentinel 삭제)
    tester.testTextInput.updateEditingValue(const TextEditingValue(text: '', selection: TextSelection.collapsed(offset: 0)));
    await tester.pump();
    expect(summary(await drain(tester)), ['down:Backspace', 'up:Backspace']);

    // Enter
    tester.testTextInput.updateEditingValue(const TextEditingValue(text: ' \n', selection: TextSelection.collapsed(offset: 2)));
    await tester.pump();
    expect(summary(await drain(tester)), ['down:Enter', 'up:Enter']);

    // 특수 키 줄의 Ctrl을 켜고 c 입력 → Ctrl+C
    await tester.tap(find.widgetWithText(OutlinedButton, 'Ctrl'));
    await tester.pump();
    tester.testTextInput.updateEditingValue(const TextEditingValue(text: ' c', selection: TextSelection.collapsed(offset: 2)));
    await tester.pump();
    expect(summary(await drain(tester)), ['down:ControlLeft', 'down:KeyC', 'up:KeyC', 'up:ControlLeft']);

    // Ctrl은 한 번 쓰면 해제됨 → 다음 c는 일반 글자
    tester.testTextInput.updateEditingValue(const TextEditingValue(text: ' c', selection: TextSelection.collapsed(offset: 2)));
    await tester.pump();
    expect(summary(await drain(tester)), ['text:c']);

    // 방향키
    await tester.tap(find.widgetWithText(OutlinedButton, '←'));
    await tester.pump();
    expect(summary(await drain(tester)), ['down:ArrowLeft', 'up:ArrowLeft']);

    await close(tester);
  });
}
