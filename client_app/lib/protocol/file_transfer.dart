// 파일 전송 (STEP 9). Windows의 FileTransfer.cs와 같은 규칙입니다.
// file_begin → file_chunk(바이너리 kind 3: [transfer_id u32][offset u64][data]) × n → file_end(sha256) → file_result

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

import 'auth.dart';
import 'connection.dart';
import 'protocol.dart';

const int fileChunkSize = 64 * 1024;

/// 파일 이름만 허용 (경로, "..", 예약 이름 거부)
String? sanitizeFileName(String? name) {
  if (name == null || name.trim().isEmpty || name.contains('/') || name.contains('\\') || name.contains(':')) {
    return null;
  }
  final cleaned = name.replaceAll(RegExp(r'[<>"|?*\x00-\x1F]'), '').trim().replaceAll(RegExp(r'\.+$'), '');
  if (cleaned.isEmpty || cleaned.length > 200 || cleaned == '.' || cleaned == '..') return null;
  final stem = cleaned.contains('.') ? cleaned.substring(0, cleaned.lastIndexOf('.')) : cleaned;
  const reserved = {'CON', 'PRN', 'AUX', 'NUL', 'COM1', 'COM2', 'COM3', 'COM4', 'LPT1', 'LPT2', 'LPT3'};
  return reserved.contains(stem.toUpperCase()) ? null : cleaned;
}

/// 폴더 안에서 겹치지 않는 경로: report.pdf → report (1).pdf
String uniquePath(String folder, String fileName) {
  final dot = fileName.lastIndexOf('.');
  final stem = dot > 0 ? fileName.substring(0, dot) : fileName;
  final ext = dot > 0 ? fileName.substring(dot) : '';
  var path = '$folder${Platform.pathSeparator}$fileName';
  for (var i = 1; File(path).existsSync(); i++) {
    path = '$folder${Platform.pathSeparator}$stem ($i)$ext';
  }
  return path;
}

class _Incoming {
  _Incoming(this.name, this.size, this.partial, this.sink, this.hashSink, this.digest);
  final String name;
  final int size;
  final File partial;
  final IOSink sink;
  final ByteConversionSink hashSink;
  final _DigestHolder digest;
  int written = 0;
}

class _DigestHolder implements Sink<Digest> {
  Digest? value;
  @override
  void add(Digest data) => value = data;
  @override
  void close() {}
}

/// Host와 파일을 주고받는 도우미
class FileTransfers {
  FileTransfers(this._connection, this.downloadFolder) {
    _subscriptions.add(_connection.controls.listen(_onControl));
    _subscriptions.add(_connection.binaries.listen(_onBinary));
  }

  final RemoteHostConnection _connection;
  final String downloadFolder;
  final List<StreamSubscription<dynamic>> _subscriptions = [];
  final Map<int, _Incoming> _incoming = {};
  final Map<int, Completer<Map<String, dynamic>>> _uploads = {};
  Completer<Map<String, dynamic>>? _list;
  Completer<Map<String, dynamic>>? _download;
  int _nextId = 2000000;

  /// 파일을 Host의 공유 폴더\Received로 보냅니다. 결과(file_result) Map을 반환합니다.
  Future<Map<String, dynamic>> upload(File file, {void Function(int sent, int total)? progress}) async {
    final id = _nextId++;
    final size = await file.length();
    final name = file.uri.pathSegments.last;
    final completer = Completer<Map<String, dynamic>>();
    _uploads[id] = completer;

    final hashResult = _DigestHolder();
    final hash = sha256.startChunkedConversion(hashResult);
    _connection.send(fileBeginMessage(id, name, size, 'upload'));
    var offset = 0;
    await for (final chunk in file.openRead()) {
      for (var start = 0; start < chunk.length; start += fileChunkSize) {
        final end = start + fileChunkSize < chunk.length ? start + fileChunkSize : chunk.length;
        final part = chunk.sublist(start, end);
        hash.add(part);
        _connection.sendRaw(encodeFileChunk(id, offset, part));
        offset += part.length;
        progress?.call(offset, size);
      }
      await _connection.flush(); // 네트워크로 나갈 때까지 기다림 (메모리에 파일 전체가 쌓이지 않도록)
    }
    hash.close();
    _connection.send(fileEndMessage(id, hexUpper(hashResult.value!.bytes)));
    try {
      return await completer.future.timeout(const Duration(minutes: 2));
    } finally {
      _uploads.remove(id);
    }
  }

  Future<List<Map<String, dynamic>>> list() async {
    _list = Completer();
    _connection.send(fileListRequestMessage());
    final result = await _list!.future.timeout(const Duration(seconds: 15));
    return ((result['files'] as List?) ?? []).cast<Map<String, dynamic>>();
  }

  /// Host 공유 폴더의 파일을 downloadFolder에 받습니다. 결과 Map (success, saved_as, error)
  Future<Map<String, dynamic>> download(String name) async {
    _download = Completer();
    _connection.send(fileDownloadMessage(name));
    return _download!.future.timeout(const Duration(minutes: 10));
  }

  void _onControl(Map<String, dynamic> m) {
    switch (m['type']) {
      case 'file_list':
        _list?.complete(m);
      case 'file_result':
        final id = (m['transfer_id'] as num).toInt();
        final upload = _uploads[id];
        if (upload != null) {
          upload.complete(m);
        } else if (_download != null && !_download!.isCompleted) {
          _download!.complete(m); // 다운로드 요청 거부 (파일 없음 등)
        }
      case 'file_begin' when m['direction'] == 'download':
        _beginDownload(m);
      case 'file_end':
        _finishDownload(m);
      case 'file_cancel':
        final id = (m['transfer_id'] as num).toInt();
        _abort(id);
        _completeDownload({'success': false, 'error': m['reason'] ?? '취소됨'});
    }
  }

  void _beginDownload(Map<String, dynamic> m) {
    final id = (m['transfer_id'] as num).toInt();
    final name = sanitizeFileName(m['name'] as String?);
    final size = (m['size'] as num?)?.toInt() ?? -1;
    if (name == null || size < 0) {
      _completeDownload({'success': false, 'error': '허용되지 않는 파일 이름입니다.'});
      return;
    }
    Directory(downloadFolder).createSync(recursive: true);
    final partial = File('$downloadFolder${Platform.pathSeparator}.${DateTime.now().microsecondsSinceEpoch}.part');
    final digest = _DigestHolder();
    _incoming[id] = _Incoming(name, size, partial, partial.openWrite(), sha256.startChunkedConversion(digest), digest);
  }

  void _onBinary(ReceivedMessage message) {
    if (message.binaryKind != kindFileChunk) return;
    final data = message.binary!;
    if (data.length < 12) return;
    final view = ByteData.sublistView(data);
    final id = view.getUint32(0);
    final offset = view.getInt64(4);
    final transfer = _incoming[id];
    if (transfer == null) return;
    final body = Uint8List.sublistView(data, 12);
    if (offset != transfer.written || transfer.written + body.length > transfer.size) {
      _abort(id);
      _completeDownload({'success': false, 'error': '전송 순서가 잘못되었습니다.'});
      return;
    }
    transfer.sink.add(body);
    transfer.hashSink.add(body);
    transfer.written += body.length;
  }

  Future<void> _finishDownload(Map<String, dynamic> m) async {
    final id = (m['transfer_id'] as num).toInt();
    final transfer = _incoming.remove(id);
    if (transfer == null) return;
    await transfer.sink.close();
    transfer.hashSink.close();
    final actual = hexUpper(transfer.digest.value!.bytes);
    if (transfer.written != transfer.size || actual != (m['sha256'] as String? ?? '').toUpperCase()) {
      await transfer.partial.delete();
      _connection.send(fileResultMessage(id, false, error: 'SHA-256 불일치'));
      _completeDownload({'success': false, 'error': '파일이 손상되었습니다.'});
      return;
    }
    final finalPath = uniquePath(downloadFolder, transfer.name);
    await transfer.partial.rename(finalPath);
    final savedAs = finalPath.split(Platform.pathSeparator).last;
    _connection.send(fileResultMessage(id, true, savedAs: savedAs));
    _completeDownload({'success': true, 'saved_as': savedAs, 'path': finalPath});
  }

  void _completeDownload(Map<String, dynamic> result) {
    if (_download != null && !_download!.isCompleted) _download!.complete(result);
  }

  void _abort(int id) {
    final transfer = _incoming.remove(id);
    if (transfer != null) {
      unawaited(transfer.sink.close().then((_) => transfer.partial.delete()).catchError((_) => transfer.partial));
    }
  }

  void dispose() {
    for (final s in _subscriptions) {
      s.cancel();
    }
    for (final id in _incoming.keys.toList()) {
      _abort(id);
    }
  }
}
