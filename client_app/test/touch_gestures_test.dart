import 'package:fake_async/fake_async.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/input/touch_gestures.dart';

class _Recorder implements TouchGestureListener {
  final events = <String>[];
  Offset scroll = Offset.zero;
  double zoom = 1;
  Offset panned = Offset.zero;

  @override
  void onTap(Offset position) => events.add('tap');
  @override
  void onTwoFingerTap() => events.add('twoTap');
  @override
  void onPanStart(Offset position) => events.add('panStart');
  @override
  void onPanUpdate(Offset delta, Offset position) => panned += delta;
  @override
  void onPanEnd() => events.add('panEnd');
  @override
  void onLongPressStart(Offset position) => events.add('longStart');
  @override
  void onLongPressMove(Offset delta, Offset position) => panned += delta;
  @override
  void onLongPressEnd() => events.add('longEnd');
  @override
  void onTwoFingerScroll(Offset delta) => scroll += delta;
  @override
  void onPinch(double scaleFactor, Offset focal) => zoom *= scaleFactor;
}

Duration ms(int v) => Duration(milliseconds: v);

void main() {
  late _Recorder r;
  late TouchGestureController c;
  setUp(() {
    r = _Recorder();
    c = TouchGestureController(r);
  });

  test('quick tap → tap', () {
    c.pointerDown(1, const Offset(100, 100), ms(0));
    c.pointerMove(1, const Offset(103, 101)); // slop 이내
    c.pointerUp(1, ms(120));
    expect(r.events, ['tap']);
  });

  test('slow press without move → no tap', () {
    fakeAsync((async) {
      c.pointerDown(1, const Offset(100, 100), ms(0));
      async.elapse(ms(400)); // long press(500ms) 전
      c.pointerUp(1, ms(400));
      expect(r.events, isEmpty);
    });
  });

  test('one finger move → pan with full delta', () {
    c.pointerDown(1, const Offset(100, 100), ms(0));
    c.pointerMove(1, const Offset(130, 100));
    c.pointerMove(1, const Offset(150, 120));
    c.pointerUp(1, ms(500));
    expect(r.events, ['panStart', 'panEnd']);
    expect(r.panned, const Offset(50, 20));
  });

  test('long press then move → drag', () {
    fakeAsync((async) {
      c.pointerDown(1, const Offset(100, 100), ms(0));
      async.elapse(ms(600));
      c.pointerMove(1, const Offset(140, 100));
      c.pointerUp(1, ms(900));
      expect(r.events, ['longStart', 'longEnd']);
      expect(r.panned, const Offset(40, 0));
    });
  });

  test('two finger tap → right click', () {
    c.pointerDown(1, const Offset(100, 100), ms(0));
    c.pointerDown(2, const Offset(200, 100), ms(20));
    c.pointerUp(1, ms(150));
    c.pointerUp(2, ms(160));
    expect(r.events, ['twoTap']);
  });

  test('two finger parallel move → scroll, not zoom', () {
    c.pointerDown(1, const Offset(100, 300), ms(0));
    c.pointerDown(2, const Offset(200, 300), ms(10));
    for (var y = 290.0; y >= 200; y -= 10) {
      c.pointerMove(1, Offset(100, y));
      c.pointerMove(2, Offset(200, y));
    }
    c.pointerUp(1, ms(400));
    c.pointerUp(2, ms(410));
    expect(r.events, isEmpty);
    expect(r.scroll.dy, closeTo(-100, 6));
    expect(r.zoom, 1);
  });

  test('pinch out → zoom in', () {
    c.pointerDown(1, const Offset(150, 300), ms(0));
    c.pointerDown(2, const Offset(250, 300), ms(10));
    c.pointerMove(1, const Offset(100, 300));
    c.pointerMove(2, const Offset(300, 300));
    c.pointerUp(1, ms(400));
    c.pointerUp(2, ms(400));
    expect(r.zoom, closeTo(2, 0.01));
    expect(r.events, isEmpty);
  });

  test('pan then second finger → pan ends, then scroll', () {
    c.pointerDown(1, const Offset(100, 100), ms(0));
    c.pointerMove(1, const Offset(150, 100));
    c.pointerDown(2, const Offset(250, 100), ms(200));
    c.pointerMove(1, const Offset(150, 150));
    c.pointerMove(2, const Offset(250, 150));
    c.pointerUp(1, ms(500));
    c.pointerUp(2, ms(500));
    expect(r.events, ['panStart', 'panEnd']);
    expect(r.scroll.dy, greaterThan(0));
  });

  test('cancel does not tap', () {
    c.pointerDown(1, const Offset(100, 100), ms(0));
    c.pointerCancel(1, ms(50));
    expect(r.events, isEmpty);
  });
}
