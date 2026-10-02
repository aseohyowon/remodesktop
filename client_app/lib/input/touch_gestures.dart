// 터치 제스처 인식 (UI와 분리된 순수 Dart 상태 기계 → 단위 테스트 가능)
//
// 한 손가락:  탭 / 이동(pan) / 길게 누르기(+이동)
// 두 손가락:  탭 / 스크롤 / 핀치
//
// 두 번 탭은 탭을 두 번 보내면 Windows가 더블 클릭으로 인식하므로 따로 기다리지 않습니다(지연 없음).

import 'dart:async';
import 'dart:math' as math;
import 'dart:ui';

abstract class TouchGestureListener {
  void onTap(Offset position);
  void onTwoFingerTap();
  void onPanStart(Offset position);
  void onPanUpdate(Offset delta, Offset position);
  void onPanEnd();
  void onLongPressStart(Offset position);
  void onLongPressMove(Offset delta, Offset position);
  void onLongPressEnd();
  void onTwoFingerScroll(Offset delta);
  void onPinch(double scaleFactor, Offset focal);
}

enum _State { idle, oneDown, panning, longPressing, twoDown, scrolling, pinching, finishing }

class TouchGestureController {
  TouchGestureController(
    this.listener, {
    this.slop = 10,
    this.longPressDelay = const Duration(milliseconds: 500),
    this.tapTimeout = const Duration(milliseconds: 300),
    this.pinchThreshold = 0.15,
  });

  final TouchGestureListener listener;

  /// 이 거리(논리 픽셀) 이하로 움직이면 탭으로 봅니다.
  final double slop;
  final Duration longPressDelay;
  final Duration tapTimeout;

  /// 두 손가락 거리가 이 비율 이상 바뀌면 핀치, 아니면 스크롤
  final double pinchThreshold;

  final Map<int, Offset> _pointers = {};
  _State _state = _State.idle;
  Timer? _longPressTimer;

  Offset _start = Offset.zero;
  Offset _last = Offset.zero;
  Duration _downTime = Duration.zero;

  Offset _twoStartCentroid = Offset.zero;
  Offset _lastCentroid = Offset.zero;
  double _startDistance = 1;
  double _lastDistance = 1;

  bool get isActive => _pointers.isNotEmpty;

  void pointerDown(int pointer, Offset position, Duration timeStamp) {
    _pointers[pointer] = position;

    if (_pointers.length == 1 && _state == _State.idle) {
      _state = _State.oneDown;
      _start = position;
      _last = position;
      _downTime = timeStamp;
      _longPressTimer = Timer(longPressDelay, _onLongPressTimer);
    } else if (_pointers.length == 2 && (_state == _State.oneDown || _state == _State.panning)) {
      if (_state == _State.panning) {
        listener.onPanEnd();
      }
      _cancelLongPress();
      _state = _State.twoDown;
      _twoStartCentroid = _centroid();
      _lastCentroid = _twoStartCentroid;
      _startDistance = math.max(_distance(), 1);
      _lastDistance = _startDistance;
    }
    // 그 외(세 손가락, 길게 누르는 중 두 번째 손가락)는 무시
  }

  void pointerMove(int pointer, Offset position) {
    if (!_pointers.containsKey(pointer)) return;
    _pointers[pointer] = position;

    switch (_state) {
      case _State.oneDown:
        if ((position - _start).distance > slop) {
          _cancelLongPress();
          _state = _State.panning;
          listener.onPanStart(_start);
          listener.onPanUpdate(position - _start, position);
          _last = position;
        }
      case _State.panning:
        listener.onPanUpdate(position - _last, position);
        _last = position;
      case _State.longPressing:
        listener.onLongPressMove(position - _last, position);
        _last = position;
      case _State.twoDown:
        if (_pointers.length < 2) return;
        final centroid = _centroid();
        final distance = _distance();
        if ((distance / _startDistance - 1).abs() > pinchThreshold) {
          _state = _State.pinching;
          listener.onPinch(distance / _startDistance, centroid);
          _lastDistance = distance;
        } else if ((centroid - _twoStartCentroid).distance > slop) {
          _state = _State.scrolling;
          listener.onTwoFingerScroll(centroid - _twoStartCentroid);
        }
        _lastCentroid = centroid;
      case _State.scrolling:
        if (_pointers.length < 2) return;
        final centroid = _centroid();
        listener.onTwoFingerScroll(centroid - _lastCentroid);
        _lastCentroid = centroid;
      case _State.pinching:
        if (_pointers.length < 2) return;
        final distance = math.max(_distance(), 1.0);
        listener.onPinch(distance / _lastDistance, _centroid());
        _lastDistance = distance;
      case _State.idle:
      case _State.finishing:
        break;
    }
  }

  void pointerUp(int pointer, Duration timeStamp) => _release(pointer, timeStamp, cancelled: false);

  void pointerCancel(int pointer, Duration timeStamp) => _release(pointer, timeStamp, cancelled: true);

  void _release(int pointer, Duration timeStamp, {required bool cancelled}) {
    if (_pointers.remove(pointer) == null) return;
    final quick = timeStamp - _downTime < tapTimeout;

    switch (_state) {
      case _State.oneDown:
        _cancelLongPress();
        if (quick && !cancelled) listener.onTap(_start);
        _state = _State.idle;
      case _State.panning:
        listener.onPanEnd();
        _state = _State.idle;
      case _State.longPressing:
        listener.onLongPressEnd();
        _state = _State.idle;
      case _State.twoDown:
        // 첫 손가락이 떨어지는 순간 판정. 나머지 손가락이 다 떨어질 때까지는 무시합니다.
        if (quick && !cancelled) listener.onTwoFingerTap();
        _state = _State.finishing;
      case _State.scrolling:
      case _State.pinching:
        _state = _State.finishing;
      case _State.idle:
      case _State.finishing:
        break;
    }

    if (_pointers.isEmpty) {
      _state = _State.idle;
    }
  }

  void _onLongPressTimer() {
    if (_state == _State.oneDown) {
      _state = _State.longPressing;
      _last = _start;
      listener.onLongPressStart(_start);
    }
  }

  void _cancelLongPress() {
    _longPressTimer?.cancel();
    _longPressTimer = null;
  }

  Offset _centroid() {
    var sum = Offset.zero;
    for (final p in _pointers.values.take(2)) {
      sum += p;
    }
    return sum / 2;
  }

  double _distance() {
    final points = _pointers.values.take(2).toList();
    return (points[0] - points[1]).distance;
  }

  void dispose() => _cancelLongPress();
}
