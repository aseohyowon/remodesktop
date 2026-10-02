// RemoteDesktop Protocol v1 (docs/protocol.md) - Dart 구현
// Windows C# 구현(windows/Protocol)과 바이트 단위로 같아야 합니다.
// Flutter 의존성이 없어 dart:io 테스트에서도 그대로 쓸 수 있습니다.

import 'dart:convert';
import 'dart:typed_data';

const int protocolVersion = 1;
const int defaultPort = 50505;
const int maxMessageBytes = 16 * 1024 * 1024;
const int maxControlMessageBytes = 64 * 1024;

const int kindControl = 1;
const int kindVideoFrame = 2;
const int kindFileChunk = 3; // [transfer_id u32][offset u64][data]
const int kindAudio = 4;

const int codecJpeg = 1;
const int codecH264 = 2;

class ProtocolException implements Exception {
  ProtocolException(this.message);
  final String message;

  @override
  String toString() => message;
}

/// 영상 프레임 (21바이트 헤더 + 압축 데이터)
class VideoFrame {
  VideoFrame({
    required this.frameId,
    required this.captureTimeMs,
    required this.width,
    required this.height,
    required this.codec,
    required this.data,
  });

  static const int headerSize = 21;

  final int frameId;
  final int captureTimeMs;
  final int width;
  final int height;
  final int codec;
  final Uint8List data;

  static VideoFrame parse(Uint8List payload) {
    if (payload.length < headerSize) {
      throw ProtocolException('영상 프레임 헤더가 너무 짧습니다.');
    }
    final header = ByteData.sublistView(payload, 0, headerSize);
    return VideoFrame(
      frameId: header.getUint32(0), // ByteData 기본값은 big-endian
      captureTimeMs: header.getInt64(4),
      width: header.getInt32(12),
      height: header.getInt32(16),
      codec: header.getUint8(20),
      data: Uint8List.sublistView(payload, headerSize),
    );
  }
}

/// 수신 메시지: control(JSON Map), video, binary(파일 조각/오디오) 중 하나. 모두 null이면 알 수 없는 종류.
class ReceivedMessage {
  ReceivedMessage.control(Map<String, dynamic> this.control)
      : video = null,
        binaryKind = null,
        binary = null;
  ReceivedMessage.video(VideoFrame this.video)
      : control = null,
        binaryKind = null,
        binary = null;
  ReceivedMessage.binary(int this.binaryKind, Uint8List this.binary)
      : control = null,
        video = null;
  ReceivedMessage.unknown()
      : control = null,
        video = null,
        binaryKind = null,
        binary = null;

  final Map<String, dynamic>? control;
  final VideoFrame? video;
  final int? binaryKind;
  final Uint8List? binary;

  String? get type => control?['type'] as String?;
}

/// [length u32][kind u8][payload] 메시지를 만듭니다.
Uint8List encodeMessage(int kind, List<int> payload) {
  final length = 1 + payload.length;
  if (length > maxMessageBytes) {
    throw ProtocolException('보내려는 메시지가 너무 큽니다: $length');
  }
  final bytes = Uint8List(4 + length);
  ByteData.sublistView(bytes).setUint32(0, length);
  bytes[4] = kind;
  bytes.setRange(5, bytes.length, payload);
  return bytes;
}

/// JSON 제어 메시지. Map의 첫 키는 반드시 'type'이어야 합니다(Windows Host 요구사항).
Uint8List encodeControl(Map<String, Object?> message) {
  assert(message.keys.first == 'type', "'type' must be the first key");
  return encodeMessage(kindControl, utf8.encode(jsonEncode(message)));
}

/// TCP로 들어오는 조각난 바이트를 완전한 메시지 단위로 잘라냅니다.
class FrameDecoder {
  Uint8List _buffer = Uint8List(256 * 1024);
  int _start = 0;
  int _end = 0;

  List<ReceivedMessage> add(List<int> chunk) {
    _ensureCapacity(chunk.length);
    _buffer.setRange(_end, _end + chunk.length, chunk);
    _end += chunk.length;

    final messages = <ReceivedMessage>[];
    while (_end - _start >= 5) {
      final length = ByteData.sublistView(_buffer, _start, _start + 4).getUint32(0);
      if (length < 1 || length > maxMessageBytes) {
        throw ProtocolException('허용되지 않는 메시지 길이입니다: $length');
      }
      if (_end - _start < 4 + length) {
        break; // 아직 다 안 들어옴
      }

      final kind = _buffer[_start + 4];
      final payload = Uint8List.fromList(Uint8List.sublistView(_buffer, _start + 5, _start + 4 + length));
      _start += 4 + length;
      messages.add(_parse(kind, payload));
    }

    if (_start == _end) {
      _start = 0;
      _end = 0;
    }
    return messages;
  }

  ReceivedMessage _parse(int kind, Uint8List payload) {
    switch (kind) {
      case kindControl:
        if (payload.length > maxControlMessageBytes) {
          throw ProtocolException('제어 메시지가 너무 큽니다.');
        }
        final decoded = jsonDecode(utf8.decode(payload));
        if (decoded is! Map<String, dynamic>) {
          throw ProtocolException('제어 메시지는 JSON 객체여야 합니다.');
        }
        return ReceivedMessage.control(decoded);
      case kindVideoFrame:
        return ReceivedMessage.video(VideoFrame.parse(payload));
      case kindFileChunk:
      case kindAudio:
        return ReceivedMessage.binary(kind, payload);
      default:
        return ReceivedMessage.unknown();
    }
  }

  void _ensureCapacity(int extra) {
    if (_end + extra <= _buffer.length) {
      return;
    }
    final used = _end - _start;
    if (used + extra <= _buffer.length) {
      _buffer.setRange(0, used, _buffer, _start); // 앞으로 당기기
    } else {
      var size = _buffer.length * 2;
      while (size < used + extra) {
        size *= 2;
      }
      final bigger = Uint8List(size);
      bigger.setRange(0, used, _buffer, _start);
      _buffer = bigger;
    }
    _start = 0;
    _end = used;
  }
}

// ---- 메시지 생성 함수 (모두 'type'이 첫 키) ----

/// codecs: 이 앱이 디코딩할 수 있는 영상 코덱. 모바일/맥 앱은 현재 JPEG만 (H.264는 Windows Client)
Map<String, Object?> helloMessage(String clientName, String platform, String clientNonce) => {
      'type': 'hello',
      'protocol_version': protocolVersion,
      'client_name': clientName,
      'platform': platform,
      'client_nonce': clientNonce,
      'codecs': ['jpeg'],
    };

Map<String, Object?> authResponseMessage(
  String proof, {
  String method = 'access_code',
  String? deviceId,
  String? totp,
  bool registerDevice = false,
  String? deviceName,
}) =>
    {
      'type': 'auth_response',
      'proof': proof,
      'method': method,
      'device_id': ?deviceId,
      'totp': ?totp,
      'register_device': registerDevice,
      'device_name': ?deviceName,
    };

Map<String, Object?> frameAckMessage(int frameId) => {'type': 'frame_ack', 'frame_id': frameId};

Map<String, Object?> byeMessage(String reason) => {'type': 'bye', 'reason': reason};

Map<String, Object?> mouseMoveMessage(double x, double y) =>
    {'type': 'mouse_move', 'x': x.clamp(0.0, 1.0), 'y': y.clamp(0.0, 1.0)};

/// button: left, right, middle, x1, x2 / action: down, up
Map<String, Object?> mouseButtonMessage(String button, String action) =>
    {'type': 'mouse_button', 'button': button, 'action': action};

Map<String, Object?> mouseWheelMessage(int deltaX, int deltaY) =>
    {'type': 'mouse_wheel', 'delta_x': deltaX, 'delta_y': deltaY};

Map<String, Object?> keyDownMessage(String code) => {'type': 'key_down', 'code': code};

Map<String, Object?> keyUpMessage(String code) => {'type': 'key_up', 'code': code};

Map<String, Object?> textInputMessage(String text) => {'type': 'text_input', 'text': text};

// ---- STEP 9 ----

Map<String, Object?> selectMonitorMessage(int index) => {'type': 'select_monitor', 'index': index};

Map<String, Object?> clipboardMessage(String text) => {'type': 'clipboard', 'text': text};

Map<String, Object?> fileListRequestMessage() => {'type': 'file_list_request'};

Map<String, Object?> fileDownloadMessage(String name) => {'type': 'file_download', 'name': name};

Map<String, Object?> fileBeginMessage(int transferId, String name, int size, String direction) =>
    {'type': 'file_begin', 'transfer_id': transferId, 'name': name, 'size': size, 'direction': direction};

Map<String, Object?> fileEndMessage(int transferId, String sha256Hex) =>
    {'type': 'file_end', 'transfer_id': transferId, 'sha256': sha256Hex};

Map<String, Object?> fileResultMessage(int transferId, bool success, {String? error, String? savedAs}) =>
    {'type': 'file_result', 'transfer_id': transferId, 'success': success, 'error': ?error, 'saved_as': ?savedAs};

Map<String, Object?> powerActionMessage(String action) => {'type': 'power_action', 'action': action};

/// Ctrl+Alt+Del (STEP 13). Host 기능 'sas'가 있을 때만 (Host가 Windows 서비스로 실행 중)
Map<String, Object?> sendSasMessage() => {'type': 'send_sas'};

Map<String, Object?> keyframeRequestMessage() => {'type': 'keyframe_request'};

// ---- STEP 12 ----

Map<String, Object?> displayModesRequestMessage() => {'type': 'display_modes_request'};

/// width/height가 0이면 원래 해상도로
Map<String, Object?> setResolutionMessage(int width, int height) => {'type': 'set_resolution', 'width': width, 'height': height};

/// 파일 조각 바이너리 메시지
Uint8List encodeFileChunk(int transferId, int offset, List<int> data) {
  final payload = Uint8List(12 + data.length);
  final view = ByteData.sublistView(payload);
  view.setUint32(0, transferId);
  view.setInt64(4, offset);
  payload.setRange(12, payload.length, data);
  return encodeMessage(kindFileChunk, payload);
}
