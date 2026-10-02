// STEP 9 파일 전송: 이름 검사(단위) + 실제 Windows Host와 주고받기(환경 변수가 있을 때)
//   $env:REMODESK_HOST="127.0.0.1"; $env:REMODESK_CODE="..."; $env:REMODESK_SHARED="C:\...\shared"; flutter test test/file_transfer_test.dart

import 'dart:io';
import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/protocol/auth.dart';
import 'package:remodesktop_client/protocol/connection.dart';
import 'package:remodesktop_client/protocol/file_transfer.dart';
import 'package:remodesktop_client/protocol/protocol.dart';

import 'support/fake_host.dart';

void main() {
  group('sanitizeFileName', () {
    test('accepts normal names', () {
      expect(sanitizeFileName('보고서 최종.pdf'), '보고서 최종.pdf');
      expect(sanitizeFileName('photo.jpg.'), 'photo.jpg');
    });

    test('rejects paths and reserved names', () {
      for (final bad in ['../host.json', '..\\x', 'C:\\Windows\\win.ini', 'a/b', 'CON', 'nul.txt', '..', '', '   ']) {
        expect(sanitizeFileName(bad), isNull, reason: bad);
      }
    });
  });

  final host = Platform.environment['REMODESK_HOST'];
  final code = Platform.environment['REMODESK_CODE'];
  final shared = Platform.environment['REMODESK_SHARED'];
  final skip = (host == null || code == null || shared == null) ? 'REMODESK_HOST / REMODESK_CODE / REMODESK_SHARED 미설정' : null;

  test('real host: features, upload, list, download', () async {
    final connection = await connectTo(host!, defaultPort, LoginRequest(method: AuthMethods.accessCode, secret: code!));
    expect(connection.hasFeature('file_transfer'), isTrue);
    expect(connection.monitors, isNotEmpty);

    // 화면 프레임에 ack를 보내야 Host가 계속 보냅니다.
    final frames = connection.frames.listen((f) => connection.ack(f.frameId));
    final downloads = Directory.systemTemp.createTempSync('rd-dart-dl-');
    final files = FileTransfers(connection, downloads.path);

    // 올리기 (64 KB 조각 여러 개)
    final source = File('${downloads.path}${Platform.pathSeparator}dart upload.bin');
    final bytes = List<int>.generate(200000, (i) => Random(i).nextInt(256));
    await source.writeAsBytes(bytes);
    final up = await files.upload(source);
    expect(up['success'], isTrue, reason: '${up['error']}');
    final saved = File('$shared${Platform.pathSeparator}Received${Platform.pathSeparator}${up['saved_as']}');
    expect(await saved.readAsBytes(), bytes);

    // 목록 + 받기
    File('$shared${Platform.pathSeparator}from-host.txt').writeAsStringSync('Host에서 온 파일');
    final list = await files.list();
    expect(list.map((f) => f['name']), contains('from-host.txt'));
    final down = await files.download('from-host.txt');
    expect(down['success'], isTrue, reason: '${down['error']}');
    expect(File(down['path'] as String).readAsStringSync(), 'Host에서 온 파일');

    // 공유 폴더 밖은 거부
    final bad = await files.download('..\\host.json');
    expect(bad['success'], isFalse);

    files.dispose();
    await frames.cancel();
    await connection.close();
  }, skip: skip, timeout: const Timeout(Duration(seconds: 60)));

  test('fake host: features list is parsed', () async {
    final fake = FakeHost();
    await fake.start();
    final connection = await connectTo('127.0.0.1', fake.port, const LoginRequest(method: AuthMethods.accessCode, secret: 'TESTCODE42'));
    expect(connection.features, isEmpty); // 가짜 Host는 기능을 알리지 않음
    expect(connection.monitors, isEmpty);
    await connection.close();
    await fake.stop();
  });
}
