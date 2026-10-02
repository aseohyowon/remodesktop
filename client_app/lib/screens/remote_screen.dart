// 원격 화면: 영상 표시 + 마우스/키보드/터치 입력
//
// macOS (및 마우스를 연결한 태블릿): 실제 마우스 포인터와 키보드로 직접 조작
// iPhone / Android 터치:
//   [터치패드 모드] 한 손가락 이동 = 커서 이동, 탭 = 클릭, 두 손가락 탭 = 우클릭,
//                  두 손가락 이동 = 스크롤, 길게 누르기 = 버튼 누른 채 드래그, 핀치 = 확대
//   [직접 터치 모드] 누른 곳을 클릭, 끌기 = 드래그, 길게 누르기 = 우클릭,
//                  두 손가락 이동 = 스크롤(확대 상태에서는 화면 이동), 핀치 = 확대
// 하단 바: [마우스 모드] [키보드] [메뉴]

import 'dart:async';
import 'dart:io';
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:file_picker/file_picker.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:path_provider/path_provider.dart';

import '../input/key_map.dart';
import '../input/soft_keyboard.dart';
import '../input/touch_gestures.dart';
import '../protocol/connection.dart';
import '../protocol/file_transfer.dart';
import '../protocol/protocol.dart';

class RemoteScreen extends StatefulWidget {
  const RemoteScreen({super.key, required this.connection, required this.title, this.reconnect});

  final RemoteHostConnection connection;
  final String title;

  /// 연결이 끊겼을 때 다시 연결하는 함수 (없으면 자동 재연결 안 함)
  final Future<RemoteHostConnection> Function()? reconnect;

  @override
  State<RemoteScreen> createState() => _RemoteScreenState();
}

class _RemoteScreenState extends State<RemoteScreen> with WidgetsBindingObserver implements TouchGestureListener {
  static bool get _isMacOS => defaultTargetPlatform == TargetPlatform.macOS;
  static bool get _isDesktop =>
      _isMacOS || defaultTargetPlatform == TargetPlatform.windows || defaultTargetPlatform == TargetPlatform.linux;
  static const double _maxZoom = 5;
  static const double _trackpadSpeed = 1.6;
  static const double _wheelPerPixel = 3;

  late RemoteHostConnection _connection = widget.connection;
  StreamSubscription<Map<String, dynamic>>? _controlSubscription;
  FileTransfers? _files;
  Map<String, dynamic>? _stats;
  String? _reconnectMessage;
  bool _reconnecting = false;

  // 화면
  StreamSubscription<VideoFrame>? _frameSubscription;
  ui.Image? _image;
  bool _decoding = false;
  VideoFrame? _pendingFrame;
  int _framesThisSecond = 0;
  int _fps = 0;
  Timer? _fpsTimer;
  bool _fullScreen = false;
  bool _leaving = false;

  // 확대/이동 (뷰포트 좌표 = offset + scale * 콘텐츠 좌표)
  double _viewScale = 1;
  Offset _viewOffset = Offset.zero;
  Size _viewportSize = Size.zero;
  Rect _imageRect = Rect.zero; // 확대 전 콘텐츠 좌표에서 원격 화면이 그려지는 위치

  // 마우스 / 터치
  int _pressedButtons = 0;
  double _wheelRemainderX = 0;
  double _wheelRemainderY = 0;
  late final TouchGestureController _touch = TouchGestureController(this);
  bool _trackpadMode = true;
  Offset _cursor = const Offset(0.5, 0.5); // 정규화 좌표
  bool _directDragging = false;
  bool _trackpadDragging = false;

  // 키보드
  final _focusNode = FocusNode(debugLabel: 'remote');
  final Set<String> _pressedKeys = {};
  bool _commandAsControl = _isMacOS;
  final _softKeyboardController = TextEditingController(text: keyboardSentinel);
  final _softKeyboardFocus = FocusNode(debugLabel: 'soft-keyboard');
  bool _softKeyboardOpen = false;
  bool _resettingSoftKeyboard = false;
  String _composing = '';
  final Set<String> _stickyModifiers = {}; // ControlLeft, AltLeft, ShiftLeft, MetaLeft

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _attach(widget.connection);
    _fpsTimer = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) {
        setState(() {
          _fps = _framesThisSecond;
          _framesThisSecond = 0;
        });
      }
    });
    _softKeyboardController.addListener(_onSoftKeyboardChanged);
    _softKeyboardFocus.addListener(() {
      if (!_softKeyboardFocus.hasFocus && _softKeyboardOpen && mounted) {
        setState(() => _softKeyboardOpen = false); // 시스템 키보드를 내린 경우
      }
    });
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _releaseAll();
    _touch.dispose();
    _fpsTimer?.cancel();
    _frameSubscription?.cancel();
    _controlSubscription?.cancel();
    _files?.dispose();
    _connection.close();
    _image?.dispose();
    _focusNode.dispose();
    _softKeyboardController.dispose();
    _softKeyboardFocus.dispose();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state != AppLifecycleState.resumed) {
      _releaseAll(); // 백그라운드로 가면 눌린 키·버튼을 모두 뗍니다.
    }
  }

  // ======================= 연결 / 자동 재연결 =======================

  void _attach(RemoteHostConnection connection) {
    _connection = connection;
    _frameSubscription = connection.frames.listen(_onFrame);
    _controlSubscription = connection.controls.listen(_onControl);
    unawaited(_downloadFolder().then((folder) {
      if (mounted && identical(_connection, connection)) _files = FileTransfers(connection, folder);
    }));
    connection.closed.then((reason) {
      if (!mounted || _leaving || !identical(_connection, connection)) return;
      if (widget.reconnect == null) {
        _leaving = true;
        Navigator.of(context).pop(reason);
      } else {
        unawaited(_reconnectLoop(reason));
      }
    });
  }

  /// 1, 2, 4, 8초... 간격으로 최대 60초 동안 다시 연결합니다. (Wi-Fi 변경, 모바일 네트워크 전환 등)
  Future<void> _reconnectLoop(String reason) async {
    if (_reconnecting) return;
    _reconnecting = true;
    _releaseAll();
    await _frameSubscription?.cancel();
    await _controlSubscription?.cancel();
    _files?.dispose();
    _files = null;

    final started = DateTime.now();
    var delay = const Duration(seconds: 1);
    for (var attempt = 1; DateTime.now().difference(started) < const Duration(seconds: 60) && mounted && !_leaving; attempt++) {
      setState(() => _reconnectMessage = '연결이 끊겼습니다.\n$reason\n\n다시 연결하는 중... ($attempt번째 시도)');
      try {
        final connection = await widget.reconnect!();
        if (!mounted || _leaving) {
          await connection.close();
          return;
        }
        _attach(connection);
        setState(() => _reconnectMessage = null);
        _reconnecting = false;
        return;
      } on ConnectionFailed catch (e) {
        reason = e.message;
        if (e.errorCode != null && e.errorCode != 'busy') break; // 인증 실패는 다시 시도해도 소용없음
      } catch (e) {
        reason = '$e';
      }
      await Future<void>.delayed(delay);
      delay = delay * 2 > const Duration(seconds: 10) ? const Duration(seconds: 10) : delay * 2;
    }

    _reconnecting = false;
    if (mounted && !_leaving) {
      _leaving = true;
      Navigator.of(context).pop('다시 연결하지 못했습니다: $reason');
    }
  }

  /// 받은 파일 저장 위치: 다운로드 폴더 → 앱 문서 폴더 → 임시 폴더 순으로 시도
  static Future<String> _downloadFolder() async {
    Directory? folder;
    try {
      folder = await getDownloadsDirectory();
    } catch (_) {}
    try {
      folder ??= await getApplicationDocumentsDirectory();
    } catch (_) {}
    folder ??= Directory.systemTemp;
    return '${folder.path}${Platform.pathSeparator}RemoteDesktop';
  }

  // ======================= 제어 메시지 (STEP 9) =======================

  void _onControl(Map<String, dynamic> message) {
    if (!mounted) return;
    switch (message['type']) {
      case 'stream_stats':
        setState(() => _stats = message);
      case 'clipboard':
        final text = message['text'] as String? ?? '';
        Clipboard.setData(ClipboardData(text: text));
        _snack('PC의 클립보드를 이 기기에 복사했습니다 (${text.length}자)');
      case 'power_result':
        _snack(message['success'] == true ? '요청을 보냈습니다: ${message['action']}' : '실패: ${message['error']}');
      case 'display_modes':
        _displayModes?.complete(message);
        _displayModes = null;
      case 'display_result':
        if (_displayModes != null) {
          _displayModes!.complete(message); // 목록 요청이 거부된 경우
          _displayModes = null;
        } else {
          final current = message['current'] as Map<String, dynamic>?;
          _snack(message['success'] == true
              ? 'PC 해상도: ${current?['width']}x${current?['height']} (연결을 끊으면 원래대로 돌아갑니다)'
              : '실패: ${message['error']}');
        }
    }
  }

  void _snack(String text) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text), duration: const Duration(seconds: 3)));
  }

  Completer<Map<String, dynamic>>? _displayModes;

  /// PC 해상도 바꾸기 (STEP 12). 연결이 끝나면 PC가 원래 해상도로 되돌립니다.
  Future<void> _chooseResolution() async {
    _displayModes = Completer();
    _connection.send(displayModesRequestMessage());
    Map<String, dynamic> reply;
    try {
      reply = await _displayModes!.future.timeout(const Duration(seconds: 10));
    } catch (_) {
      _displayModes = null;
      _snack('해상도 목록을 받지 못했습니다.');
      return;
    }
    if (reply['type'] != 'display_modes') {
      _snack('${reply['error'] ?? '해상도를 바꿀 수 없습니다.'}');
      return;
    }
    if (!mounted) return;

    String label(Map<String, dynamic> m) => '${m['width']} x ${m['height']}';
    final original = reply['original'] as Map<String, dynamic>;
    final current = reply['current'] as Map<String, dynamic>;
    final modes = ((reply['modes'] as List?) ?? []).cast<Map<String, dynamic>>();
    final chosen = await showDialog<Map<String, dynamic>>(
      context: context,
      builder: (context) => SimpleDialog(
        title: const Text('PC 해상도'),
        children: [
          SimpleDialogOption(
            onPressed: () => Navigator.pop(context, {'width': 0, 'height': 0}),
            child: Text('원래대로 (${label(original)})'),
          ),
          const Divider(),
          for (final m in modes)
            SimpleDialogOption(
              onPressed: () => Navigator.pop(context, m),
              child: Text(label(m) + (m['width'] == current['width'] && m['height'] == current['height'] ? '  ✓' : '')),
            ),
        ],
      ),
    );
    if (chosen == null) return;
    _connection.send(setResolutionMessage((chosen['width'] as num).toInt(), (chosen['height'] as num).toInt()));
  }

  Future<void> _sendClipboard() async {
    final data = await Clipboard.getData(Clipboard.kTextPlain);
    final text = data?.text;
    if (text == null || text.isEmpty) {
      _snack('이 기기의 클립보드에 텍스트가 없습니다.');
      return;
    }
    _connection.send(clipboardMessage(text.length > 32000 ? text.substring(0, 32000) : text));
    _snack('클립보드를 PC로 보냈습니다. PC에서 Ctrl+V로 붙여넣으세요.');
  }

  Future<void> _chooseMonitor() async {
    final monitors = _connection.monitors;
    final index = await showDialog<int>(
      context: context,
      builder: (context) => SimpleDialog(
        title: const Text('볼 모니터'),
        children: [
          for (final m in monitors)
            SimpleDialogOption(
              onPressed: () => Navigator.pop(context, (m['index'] as num).toInt()),
              child: Text('모니터 ${(m['index'] as num).toInt() + 1}  (${m['width']}x${m['height']})${m['primary'] == true ? '  주 모니터' : ''}'),
            ),
          if (monitors.length > 1)
            SimpleDialogOption(onPressed: () => Navigator.pop(context, -1), child: const Text('모든 모니터')),
        ],
      ),
    );
    if (index != null) _connection.send(selectMonitorMessage(index));
  }

  Future<void> _power(String action, String question) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('전원'),
        content: Text(question),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('취소')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('실행')),
        ],
      ),
    );
    if (ok == true) _connection.send(powerActionMessage(action));
  }

  Future<void> _uploadFile() async {
    final files = _files;
    if (files == null) return;
    final picked = await FilePicker.pickFiles(dialogTitle: 'PC로 보낼 파일');
    final path = picked.isEmpty ? null : picked.first.path;
    if (path == null) return;
    _snack('보내는 중: ${picked.first.name}');
    try {
      final result = await files.upload(File(path));
      _snack(result['success'] == true
          ? '보냈습니다: ${result['saved_as']} (PC의 공유 폴더\\Received)'
          : '실패: ${result['error']}');
    } catch (e) {
      _snack('실패: $e');
    }
  }

  Future<void> _downloadFile() async {
    final files = _files;
    if (files == null) return;
    List<Map<String, dynamic>> list;
    try {
      list = await files.list();
    } catch (e) {
      _snack('파일 목록을 받지 못했습니다: $e');
      return;
    }
    if (!mounted) return;
    final name = await showDialog<String>(
      context: context,
      builder: (context) => SimpleDialog(
        title: const Text('PC에서 받을 파일'),
        children: [
          if (list.isEmpty) const Padding(padding: EdgeInsets.all(16), child: Text('PC의 공유 폴더가 비어 있습니다.')),
          for (final f in list)
            SimpleDialogOption(
              onPressed: () => Navigator.pop(context, f['name'] as String),
              child: Text('${f['name']}  (${((f['size'] as num) / 1024).toStringAsFixed(0)} KB)'),
            ),
        ],
      ),
    );
    if (name == null) return;
    _snack('받는 중: $name');
    final result = await files.download(name);
    _snack(result['success'] == true ? '저장했습니다: ${result['path']}' : '실패: ${result['error']}');
  }

  String get _qualityText {
    final s = _stats;
    if (s == null) return '';
    final rtt = (s['rtt_ms'] as num?)?.toInt() ?? 0;
    final level = (s['quality_level'] as num?)?.toInt() ?? 0;
    final label = rtt < 50 && level == 0 ? '좋음' : (rtt < 150 && level <= 2 ? '보통' : '나쁨');
    final mbps = ((s['kbps'] as num?) ?? 0) / 1000;
    return '연결 $label · ${mbps.toStringAsFixed(1)} Mbps · $rtt ms';
  }

  // ======================= 영상 =======================

  /// JPEG를 순서대로 디코딩합니다. 디코딩 중 새 프레임이 오면 최신 것만 남기고
  /// 건너뛴 프레임은 바로 ack 해서 Host가 멈추지 않게 합니다.
  Future<void> _onFrame(VideoFrame frame) async {
    if (frame.codec != codecJpeg) {
      _connection.ack(frame.frameId);
      return;
    }
    if (_decoding) {
      final skipped = _pendingFrame;
      if (skipped != null) _connection.ack(skipped.frameId);
      _pendingFrame = frame;
      return;
    }

    _decoding = true;
    VideoFrame? current = frame;
    while (current != null) {
      try {
        final codec = await ui.instantiateImageCodec(current.data);
        final decoded = await codec.getNextFrame();
        codec.dispose();
        if (!mounted) {
          decoded.image.dispose();
          return;
        }
        final previous = _image;
        setState(() => _image = decoded.image);
        WidgetsBinding.instance.addPostFrameCallback((_) => previous?.dispose());
        _framesThisSecond++;
      } catch (_) {
        // 손상된 프레임은 건너뜀
      }
      _connection.ack(current.frameId);
      current = _pendingFrame;
      _pendingFrame = null;
    }
    _decoding = false;
  }

  // ======================= 좌표 =======================

  /// 뷰포트 좌표 → 원격 화면 0~1 좌표
  Offset? _normalize(Offset viewportPoint, {required bool clamp}) {
    if (_imageRect.isEmpty) return null;
    final content = (viewportPoint - _viewOffset) / _viewScale;
    if (!clamp && !_imageRect.contains(content)) return null;
    return Offset(
      ((content.dx - _imageRect.left) / _imageRect.width).clamp(0.0, 1.0),
      ((content.dy - _imageRect.top) / _imageRect.height).clamp(0.0, 1.0),
    );
  }

  /// 원격 화면 0~1 좌표 → 뷰포트 좌표
  Offset _toViewport(Offset normalized) {
    final content = Offset(
      _imageRect.left + normalized.dx * _imageRect.width,
      _imageRect.top + normalized.dy * _imageRect.height,
    );
    return _viewOffset + content * _viewScale;
  }

  void _zoom(double factor, Offset focal) {
    final newScale = (_viewScale * factor).clamp(1.0, _maxZoom);
    final ratio = newScale / _viewScale;
    setState(() {
      _viewOffset = focal - (focal - _viewOffset) * ratio;
      _viewScale = newScale;
      _clampView();
    });
  }

  void _panView(Offset delta) {
    setState(() {
      _viewOffset += delta;
      _clampView();
    });
  }

  void _clampView() {
    final minX = _viewportSize.width - _viewportSize.width * _viewScale;
    final minY = _viewportSize.height - _viewportSize.height * _viewScale;
    _viewOffset = Offset(_viewOffset.dx.clamp(minX, 0.0), _viewOffset.dy.clamp(minY, 0.0));
  }

  /// 터치패드 모드에서 확대 중이면 커서가 화면 밖으로 나가지 않도록 따라갑니다.
  void _keepCursorVisible() {
    if (_viewScale <= 1) return;
    const margin = 48.0;
    final p = _toViewport(_cursor);
    var dx = 0.0;
    var dy = 0.0;
    if (p.dx < margin) dx = margin - p.dx;
    if (p.dx > _viewportSize.width - margin) dx = _viewportSize.width - margin - p.dx;
    if (p.dy < margin) dy = margin - p.dy;
    if (p.dy > _viewportSize.height - margin) dy = _viewportSize.height - margin - p.dy;
    if (dx != 0 || dy != 0) _panView(Offset(dx, dy));
  }

  // ======================= 마우스 공통 =======================

  void _sendCursor() => _connection.send(mouseMoveMessage(_cursor.dx, _cursor.dy));

  void _click(String button, {int count = 1}) {
    for (var i = 0; i < count; i++) {
      _connection.send(mouseButtonMessage(button, 'down'));
      _connection.send(mouseButtonMessage(button, 'up'));
    }
  }

  void _sendWheel(double dx, double dy) {
    _wheelRemainderX += dx;
    _wheelRemainderY += dy;
    final x = _wheelRemainderX.truncate();
    final y = _wheelRemainderY.truncate();
    if (x == 0 && y == 0) return;
    _wheelRemainderX -= x;
    _wheelRemainderY -= y;
    _connection.send(mouseWheelMessage(x, y));
  }

  // ======================= 실제 마우스 (macOS 등) =======================

  static const _buttonNames = {
    kPrimaryMouseButton: 'left',
    kSecondaryMouseButton: 'right',
    kMiddleMouseButton: 'middle',
    kBackMouseButton: 'x1',
    kForwardMouseButton: 'x2',
  };

  bool _isTouch(PointerEvent event) =>
      !_isDesktop && (event.kind == PointerDeviceKind.touch || event.kind == PointerDeviceKind.stylus);

  bool _sendMouseMove(Offset local) {
    final p = _normalize(local, clamp: _pressedButtons != 0);
    if (p == null) return false;
    _cursor = p;
    _sendCursor();
    return true;
  }

  void _onPointerDown(PointerDownEvent event) {
    if (_isTouch(event)) {
      _touch.pointerDown(event.pointer, event.localPosition, event.timeStamp);
      return;
    }
    if (!_softKeyboardOpen) _focusNode.requestFocus();
    if (!_sendMouseMove(event.localPosition)) return;
    final newlyPressed = event.buttons & ~_pressedButtons;
    _buttonNames.forEach((bit, name) {
      if (newlyPressed & bit != 0) {
        _pressedButtons |= bit;
        _connection.send(mouseButtonMessage(name, 'down'));
      }
    });
  }

  void _onPointerUp(PointerUpEvent event) {
    if (_isTouch(event)) {
      _touch.pointerUp(event.pointer, event.timeStamp);
      return;
    }
    final released = _pressedButtons & ~event.buttons;
    _sendMouseMove(event.localPosition);
    _buttonNames.forEach((bit, name) {
      if (released & bit != 0) {
        _pressedButtons &= ~bit;
        _connection.send(mouseButtonMessage(name, 'up'));
      }
    });
  }

  void _onPointerCancel(PointerCancelEvent event) {
    if (_isTouch(event)) _touch.pointerCancel(event.pointer, event.timeStamp);
  }

  void _onPointerMove(PointerMoveEvent event) {
    if (_isTouch(event)) {
      _touch.pointerMove(event.pointer, event.localPosition);
    } else {
      _sendMouseMove(event.localPosition);
    }
  }

  void _onPointerHover(PointerHoverEvent event) => _sendMouseMove(event.localPosition);

  void _onPointerSignal(PointerSignalEvent event) {
    if (event is PointerScrollEvent) {
      // scrollDelta: 양수 = 오른쪽/아래로 스크롤. 프로토콜: delta_x 양수 = 오른쪽, delta_y 양수 = 위
      _sendWheel(event.scrollDelta.dx * 3, -event.scrollDelta.dy * 3);
    }
  }

  /// Mac 트랙패드 두 손가락 스크롤
  void _onPanZoomUpdate(PointerPanZoomUpdateEvent event) {
    _sendWheel(-event.panDelta.dx * 2, event.panDelta.dy * 2);
  }

  // ======================= 터치 제스처 (TouchGestureListener) =======================

  void _moveCursorBy(Offset delta) {
    if (_imageRect.isEmpty) return;
    final scale = _imageRect.width * _viewScale;
    _cursor = Offset(
      (_cursor.dx + delta.dx * _trackpadSpeed / scale).clamp(0.0, 1.0),
      (_cursor.dy + delta.dy * _trackpadSpeed * _imageRect.width / _imageRect.height / scale).clamp(0.0, 1.0),
    );
    _sendCursor();
    _keepCursorVisible();
  }

  @override
  void onTap(Offset position) {
    if (!_trackpadMode) {
      final p = _normalize(position, clamp: false);
      if (p == null) return;
      _cursor = p;
    }
    _sendCursor();
    _click('left');
  }

  @override
  void onTwoFingerTap() {
    _sendCursor();
    _click('right');
  }

  @override
  void onPanStart(Offset position) {
    if (_trackpadMode) return;
    final p = _normalize(position, clamp: false);
    if (p == null) return;
    _cursor = p;
    _sendCursor();
    _connection.send(mouseButtonMessage('left', 'down'));
    _directDragging = true;
  }

  @override
  void onPanUpdate(Offset delta, Offset position) {
    if (_trackpadMode) {
      _moveCursorBy(delta);
    } else if (_directDragging) {
      _cursor = _normalize(position, clamp: true) ?? _cursor;
      _sendCursor();
    }
  }

  @override
  void onPanEnd() {
    if (_directDragging) {
      _connection.send(mouseButtonMessage('left', 'up'));
      _directDragging = false;
    }
  }

  @override
  void onLongPressStart(Offset position) {
    HapticFeedback.mediumImpact();
    if (_trackpadMode) {
      // 버튼을 누른 채로 두고, 손가락을 움직이면 드래그
      _sendCursor();
      _connection.send(mouseButtonMessage('left', 'down'));
      _trackpadDragging = true;
    } else {
      final p = _normalize(position, clamp: false);
      if (p == null) return;
      _cursor = p;
      _sendCursor();
      _click('right');
    }
  }

  @override
  void onLongPressMove(Offset delta, Offset position) {
    if (_trackpadDragging) _moveCursorBy(delta);
  }

  @override
  void onLongPressEnd() {
    if (_trackpadDragging) {
      _connection.send(mouseButtonMessage('left', 'up'));
      _trackpadDragging = false;
    }
  }

  @override
  void onTwoFingerScroll(Offset delta) {
    if (!_trackpadMode && _viewScale > 1) {
      _panView(delta); // 직접 터치 + 확대 상태: 화면 이동
    } else {
      // 휴대폰 스크롤과 같은 방향: 손가락을 위로 → 아래 내용, 왼쪽으로 → 오른쪽 내용
      _sendWheel(-delta.dx * _wheelPerPixel, delta.dy * _wheelPerPixel);
    }
  }

  @override
  void onPinch(double scaleFactor, Offset focal) => _zoom(scaleFactor, focal);

  // ======================= 하드웨어 키보드 =======================

  KeyEventResult _onKeyEvent(FocusNode node, KeyEvent event) {
    if (_softKeyboardOpen) return KeyEventResult.ignored; // 가상 키보드가 처리
    final code = protocolKeyCode(event.physicalKey, commandAsControl: _commandAsControl);
    if (code == null) return KeyEventResult.ignored;

    if (event is KeyUpEvent) {
      _pressedKeys.remove(code);
      _connection.send(keyUpMessage(code));
    } else {
      _pressedKeys.add(code);
      _connection.send(keyDownMessage(code));
    }
    return KeyEventResult.handled;
  }

  // ======================= 가상 키보드 =======================

  void _toggleSoftKeyboard() {
    setState(() => _softKeyboardOpen = !_softKeyboardOpen);
    if (_softKeyboardOpen) {
      _softKeyboardFocus.requestFocus();
      SystemChannels.textInput.invokeMethod<void>('TextInput.show');
    } else {
      _softKeyboardFocus.unfocus();
      _stickyModifiers.clear();
      _focusNode.requestFocus();
    }
  }

  void _onSoftKeyboardChanged() {
    if (_resettingSoftKeyboard) return;
    final value = _softKeyboardController.value;

    // 한글 등 조합 중이면 완성될 때까지 기다립니다.
    if (value.composing.isValid && !value.composing.isCollapsed) {
      setState(() => _composing = value.composing.textInside(value.text));
      return;
    }

    for (final action in diffKeyboardText(value.text)) {
      switch (action) {
        case TypeText(:final text):
          _typeText(text);
        case PressKey(:final code):
          _pressKey(code);
      }
    }

    _resettingSoftKeyboard = true;
    _softKeyboardController.value = const TextEditingValue(
      text: keyboardSentinel,
      selection: TextSelection.collapsed(offset: keyboardSentinel.length),
    );
    _resettingSoftKeyboard = false;
    if (_composing.isNotEmpty) setState(() => _composing = '');
  }

  void _typeText(String text) {
    if (_stickyModifiers.isEmpty) {
      _connection.send(textInputMessage(text));
      return;
    }
    // Ctrl/Alt 등을 켠 상태면 글자를 키 위치로 바꿔 조합키로 보냅니다. (Ctrl + c → Ctrl+C)
    for (final character in text.characters) {
      final code = characterToKeyCode(character);
      if (code != null) {
        _pressKey(code);
      } else {
        _connection.send(textInputMessage(character));
      }
    }
  }

  /// 키 한 번 누르기. 켜져 있는 수정 키(Ctrl 등)와 함께 누르고, 수정 키는 한 번 쓰면 해제됩니다.
  void _pressKey(String code) {
    final modifiers = _stickyModifiers.toList();
    for (final m in modifiers) {
      _connection.send(keyDownMessage(m));
    }
    _connection.send(keyDownMessage(code));
    _connection.send(keyUpMessage(code));
    for (final m in modifiers.reversed) {
      _connection.send(keyUpMessage(m));
    }
    if (modifiers.isNotEmpty) setState(_stickyModifiers.clear);
  }

  void _toggleModifier(String code) {
    setState(() {
      if (!_stickyModifiers.remove(code)) _stickyModifiers.add(code);
    });
  }

  void _tapKeys(List<String> codes) {
    for (final code in codes) {
      _connection.send(keyDownMessage(code));
    }
    for (final code in codes.reversed) {
      _connection.send(keyUpMessage(code));
    }
  }

  void _releaseAll() {
    for (final code in _pressedKeys) {
      _connection.send(keyUpMessage(code));
    }
    _pressedKeys.clear();
    _buttonNames.forEach((bit, name) {
      if (_pressedButtons & bit != 0) _connection.send(mouseButtonMessage(name, 'up'));
    });
    _pressedButtons = 0;
    if (_directDragging || _trackpadDragging) {
      _connection.send(mouseButtonMessage('left', 'up'));
      _directDragging = false;
      _trackpadDragging = false;
    }
  }

  // ======================= 메뉴 =======================

  void _onMenu(String action) {
    switch (action) {
      case 'lang':
        _tapKeys(['Lang1']);
      case 'win':
        _tapKeys(['MetaLeft']);
      case 'alttab':
        _tapKeys(['AltLeft', 'Tab']);
      case 'esc':
        _tapKeys(['Escape']);
      case 'cad':
        ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
          content: Text('Ctrl+Alt+Del은 Windows 보안 정책상 원격 입력으로 보낼 수 없습니다.'),
        ));
      case 'cmd':
        setState(() => _commandAsControl = !_commandAsControl);
      case 'monitor':
        unawaited(_chooseMonitor());
      case 'resolution':
        unawaited(_chooseResolution());
      case 'clip':
        unawaited(_sendClipboard());
      case 'upload':
        unawaited(_uploadFile());
      case 'download':
        unawaited(_downloadFile());
      case 'p_lock':
        unawaited(_power('lock', 'PC 화면을 잠글까요?'));
      case 'p_logoff':
        unawaited(_power('logoff', 'PC에서 로그아웃할까요?\n저장하지 않은 작업은 사라질 수 있습니다.'));
      case 'p_restart':
        unawaited(_power('restart', 'PC를 다시 시작할까요?'));
      case 'p_shutdown':
        unawaited(_power('shutdown', 'PC를 종료할까요?\n원격으로 다시 켤 수 없습니다.'));
      case 'zoomreset':
        setState(() {
          _viewScale = 1;
          _viewOffset = Offset.zero;
        });
      case 'help':
        _showGestureHelp();
      case 'full':
        _setFullScreen(!_fullScreen);
      case 'close':
        _leaving = true;
        Navigator.of(context).pop('연결을 종료했습니다.');
    }
    if (!_softKeyboardOpen) _focusNode.requestFocus();
  }

  void _setFullScreen(bool value) {
    setState(() => _fullScreen = value);
    SystemChrome.setEnabledSystemUIMode(value ? SystemUiMode.immersiveSticky : SystemUiMode.edgeToEdge);
  }

  void _showGestureHelp() {
    showModalBottomSheet<void>(
      context: context,
      builder: (_) => const SafeArea(
        child: Padding(
          padding: EdgeInsets.all(20),
          child: Text(
            '터치패드 모드\n'
            '• 한 손가락 이동: 커서 이동\n'
            '• 탭: 클릭 / 두 번 탭: 더블 클릭\n'
            '• 두 손가락 탭: 우클릭\n'
            '• 두 손가락 이동: 스크롤\n'
            '• 길게 누른 뒤 이동: 드래그\n'
            '• 두 손가락 벌리기: 확대\n\n'
            '직접 터치 모드\n'
            '• 누른 곳 클릭, 끌어서 드래그\n'
            '• 길게 누르기: 우클릭\n'
            '• 확대 상태에서 두 손가락 이동: 화면 이동',
            style: TextStyle(height: 1.6),
          ),
        ),
      ),
    );
  }

  // ======================= UI =======================

  @override
  Widget build(BuildContext context) {
    final image = _image;
    final remoteSize = image != null
        ? Size(image.width.toDouble(), image.height.toDouble())
        : Size(_connection.screenWidth.toDouble(), _connection.screenHeight.toDouble());

    final viewport = LayoutBuilder(builder: (context, constraints) {
      final size = constraints.biggest;
      if (size != _viewportSize) {
        _viewportSize = size;
        _clampView();
      }
      _imageRect = _fitRect(remoteSize, size);

      return Listener(
        key: const ValueKey('remote-viewport'),
        behavior: HitTestBehavior.opaque,
        onPointerDown: _onPointerDown,
        onPointerUp: _onPointerUp,
        onPointerCancel: _onPointerCancel,
        onPointerMove: _onPointerMove,
        onPointerHover: _onPointerHover,
        onPointerSignal: _onPointerSignal,
        onPointerPanZoomUpdate: _isDesktop ? _onPanZoomUpdate : null,
        child: ClipRect(
          child: Stack(children: [
            const Positioned.fill(child: ColoredBox(color: Colors.black)),
            Positioned.fill(
              child: Transform(
                transform: Matrix4.identity()
                  ..translateByDouble(_viewOffset.dx, _viewOffset.dy, 0, 1)
                  ..scaleByDouble(_viewScale, _viewScale, 1, 1),
                child: Stack(children: [
                  if (image != null)
                    Positioned.fromRect(
                      rect: _imageRect,
                      child: RawImage(image: image, fit: BoxFit.fill, filterQuality: FilterQuality.medium),
                    )
                  else
                    const Center(child: Text('화면을 기다리는 중...', style: TextStyle(color: Colors.white54))),
                ]),
              ),
            ),
          ]),
        ),
      );
    });

    return PopScope(
      onPopInvokedWithResult: (didPop, _) => _leaving = true,
      child: Scaffold(
        backgroundColor: Colors.black,
        resizeToAvoidBottomInset: true,
        appBar: _fullScreen
            ? null
            : AppBar(
                title: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('${widget.title}  ·  ${remoteSize.width.toInt()}x${remoteSize.height.toInt()}  ·  $_fps fps',
                        style: const TextStyle(fontSize: 15)),
                    if (_qualityText.isNotEmpty) Text(_qualityText, style: const TextStyle(fontSize: 11)),
                  ],
                ),
                actions: [
                  IconButton(tooltip: '전체 화면', icon: const Icon(Icons.fullscreen), onPressed: () => _onMenu('full')),
                  if (_isDesktop) _buildMenuButton(),
                ],
              ),
        body: Focus(
          focusNode: _focusNode,
          autofocus: true,
          onKeyEvent: _onKeyEvent,
          child: Column(children: [
            Expanded(
              child: Stack(children: [
                Positioned.fill(child: viewport),
                // 보이지 않는 입력란: 가상 키보드를 띄우고 입력을 받습니다.
                // 조건부 위젯보다 앞에 두고 key를 줘야 다시 만들어지지 않습니다(한글 조합이 끊기지 않게).
                Positioned(
                  key: const ValueKey('soft-keyboard-host'),
                  left: 0,
                  bottom: 0,
                  width: 1,
                  height: 1,
                  child: Opacity(
                    opacity: 0,
                    child: TextField(
                      key: const ValueKey('soft-keyboard'),
                      controller: _softKeyboardController,
                      focusNode: _softKeyboardFocus,
                      autocorrect: false,
                      enableSuggestions: false,
                      enableIMEPersonalizedLearning: false,
                      smartDashesType: SmartDashesType.disabled,
                      smartQuotesType: SmartQuotesType.disabled,
                      keyboardType: TextInputType.multiline,
                      textInputAction: TextInputAction.newline,
                      maxLines: null,
                    ),
                  ),
                ),
                if (_fullScreen)
                  Positioned(
                    top: 8,
                    right: 8,
                    child: SafeArea(
                      child: IconButton.filledTonal(
                        tooltip: '전체 화면 종료',
                        icon: const Icon(Icons.fullscreen_exit),
                        onPressed: () => _onMenu('full'),
                      ),
                    ),
                  ),
                if (_reconnectMessage != null)
                  Positioned.fill(
                    child: ColoredBox(
                      color: Colors.black54,
                      child: Center(
                        child: Text(_reconnectMessage!, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white, fontSize: 16)),
                      ),
                    ),
                  ),
                if (_composing.isNotEmpty)
                  Positioned(
                    left: 12,
                    bottom: 12,
                    child: Chip(label: Text('입력 중: $_composing')),
                  ),
              ]),
            ),
            if (!_isDesktop && _softKeyboardOpen) _buildSpecialKeys(),
            if (!_isDesktop) _buildBottomBar(),
          ]),
        ),
      ),
    );
  }

  Widget _buildBottomBar() {
    return Material(
      color: Theme.of(context).colorScheme.surfaceContainer,
      child: SafeArea(
        top: false,
        child: SizedBox(
          height: 52,
          child: Row(children: [
            Expanded(
              child: TextButton.icon(
                onPressed: () => setState(() => _trackpadMode = !_trackpadMode),
                icon: Icon(_trackpadMode ? Icons.touch_app_outlined : Icons.ads_click),
                label: Text(_trackpadMode ? '터치패드' : '직접 터치'),
              ),
            ),
            Expanded(
              child: TextButton.icon(
                onPressed: _toggleSoftKeyboard,
                icon: Icon(_softKeyboardOpen ? Icons.keyboard_hide : Icons.keyboard),
                label: const Text('키보드'),
              ),
            ),
            Expanded(child: _buildMenuButton(asTextButton: true)),
          ]),
        ),
      ),
    );
  }

  Widget _buildSpecialKeys() {
    Widget key(String label, VoidCallback onTap, {bool active = false}) => Padding(
          padding: const EdgeInsets.symmetric(horizontal: 2),
          child: active
              ? FilledButton(onPressed: onTap, style: _keyStyle, child: Text(label))
              : OutlinedButton(onPressed: onTap, style: _keyStyle, child: Text(label)),
        );
    Widget modifier(String label, String code) =>
        key(label, () => _toggleModifier(code), active: _stickyModifiers.contains(code));

    return Material(
      color: Theme.of(context).colorScheme.surfaceContainerHigh,
      child: SizedBox(
        height: 44,
        child: ListView(
          scrollDirection: Axis.horizontal,
          padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 4),
          children: [
            key('Esc', () => _pressKey('Escape')),
            key('Tab', () => _pressKey('Tab')),
            modifier('Ctrl', 'ControlLeft'),
            modifier('Alt', 'AltLeft'),
            modifier('Shift', 'ShiftLeft'),
            modifier('Win', 'MetaLeft'),
            key('한/영', () => _pressKey('Lang1')),
            key('←', () => _pressKey('ArrowLeft')),
            key('↑', () => _pressKey('ArrowUp')),
            key('↓', () => _pressKey('ArrowDown')),
            key('→', () => _pressKey('ArrowRight')),
            key('Del', () => _pressKey('Delete')),
            key('Home', () => _pressKey('Home')),
            key('End', () => _pressKey('End')),
            key('PgUp', () => _pressKey('PageUp')),
            key('PgDn', () => _pressKey('PageDown')),
            for (var i = 1; i <= 12; i++) key('F$i', () => _pressKey('F$i')),
          ],
        ),
      ),
    );
  }

  static final ButtonStyle _keyStyle = ButtonStyle(
    padding: WidgetStateProperty.all(const EdgeInsets.symmetric(horizontal: 10)),
    minimumSize: WidgetStateProperty.all(const Size(44, 34)),
    visualDensity: VisualDensity.compact,
  );

  Widget _buildMenuButton({bool asTextButton = false}) {
    return PopupMenuButton<String>(
      onSelected: _onMenu,
      itemBuilder: (_) => [
        const PopupMenuItem(value: 'lang', child: Text('한/영 전환')),
        const PopupMenuItem(value: 'win', child: Text('Windows 키')),
        const PopupMenuItem(value: 'alttab', child: Text('Alt+Tab')),
        const PopupMenuItem(value: 'esc', child: Text('Esc')),
        const PopupMenuItem(value: 'cad', child: Text('Ctrl+Alt+Del')),
        if (_isMacOS)
          CheckedPopupMenuItem(value: 'cmd', checked: _commandAsControl, child: const Text('⌘ Command → Ctrl')),
        if (_connection.monitors.isNotEmpty) const PopupMenuItem(value: 'monitor', child: Text('모니터 선택')),
        if (_connection.hasFeature('display')) const PopupMenuItem(value: 'resolution', child: Text('PC 해상도')),
        if (_connection.hasFeature('clipboard')) const PopupMenuItem(value: 'clip', child: Text('내 클립보드를 PC로 보내기')),
        if (_connection.hasFeature('file_transfer')) ...[
          const PopupMenuItem(value: 'upload', child: Text('파일 보내기')),
          const PopupMenuItem(value: 'download', child: Text('파일 받기')),
        ],
        if (_connection.hasFeature('power')) ...[
          const PopupMenuDivider(),
          const PopupMenuItem(value: 'p_lock', child: Text('PC 화면 잠금')),
          const PopupMenuItem(value: 'p_logoff', child: Text('로그아웃')),
          const PopupMenuItem(value: 'p_restart', child: Text('다시 시작')),
          const PopupMenuItem(value: 'p_shutdown', child: Text('종료')),
        ],
        if (!_isDesktop) ...[
          const PopupMenuDivider(),
          const PopupMenuItem(value: 'zoomreset', child: Text('확대 초기화')),
          const PopupMenuItem(value: 'help', child: Text('터치 사용법')),
        ],
        const PopupMenuDivider(),
        const PopupMenuItem(value: 'close', child: Text('연결 종료')),
      ],
      child: asTextButton
          ? Row(mainAxisAlignment: MainAxisAlignment.center, children: [
              Icon(Icons.menu, size: 18, color: Theme.of(context).colorScheme.primary),
              const SizedBox(width: 8),
              Text('메뉴', style: TextStyle(color: Theme.of(context).colorScheme.primary)),
            ])
          : null,
    );
  }

  /// 원격 화면 비율을 유지하면서 영역 안에 맞춘 사각형 (가운데 정렬)
  static Rect _fitRect(Size image, Size area) {
    if (image.isEmpty || area.isEmpty) return Rect.zero;
    final scale = math.min(area.width / image.width, area.height / image.height);
    final width = image.width * scale;
    final height = image.height * scale;
    return Rect.fromLTWH((area.width - width) / 2, (area.height - height) / 2, width, height);
  }
}
