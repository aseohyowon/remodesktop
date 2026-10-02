// 실제 Windows Host에 연결하는 통합 테스트. 환경 변수가 없으면 건너뜁니다.
//   $env:REMODESK_HOST = "127.0.0.1"; $env:REMODESK_CODE = "접속코드"; flutter test test/host_e2e_test.dart

import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/protocol/auth.dart';
import 'package:remodesktop_client/protocol/connection.dart';
import 'package:remodesktop_client/protocol/protocol.dart';

import 'support/fake_host.dart';

void main() {
  final host = Platform.environment['REMODESK_HOST'];
  final code = Platform.environment['REMODESK_CODE'];
  final skip = (host == null || code == null) ? 'REMODESK_HOST / REMODESK_CODE 미설정' : null;

  test('wrong access code is rejected', () async {
    final known = MemoryKnownHosts();
    await expectLater(
      connectTo(host!, defaultPort, const LoginRequest(method: AuthMethods.accessCode, secret: 'WRONGCODE1'), knownHosts: known),
      throwsA(isA<ConnectionFailed>().having((e) => e.errorCode, 'code', AuthErrorCodes.invalidCredentials)),
    );
    expect(known.map, isEmpty); // 인증 실패 시 지문을 저장하지 않음
  }, skip: skip);

  test('connect, receive frames, close, reconnect', () async {
    final known = MemoryKnownHosts();
    final prompts = TestPrompts();
    final connection = await connectTo(host!, defaultPort,
        LoginRequest(method: AuthMethods.accessCode, secret: code!.toLowerCase()), knownHosts: known, prompts: prompts);
    expect(prompts.newHostPrompts, 1);
    expect(connection.screenWidth, greaterThan(0));

    var count = 0;
    final enough = Completer<void>();
    final sub = connection.frames.listen((frame) {
      expect(frame.data.sublist(0, 2), [0xFF, 0xD8]); // JPEG
      count++;
      connection.ack(frame.frameId);
      if (count == 10 && !enough.isCompleted) enough.complete();
    });
    await enough.future.timeout(const Duration(seconds: 10));
    await sub.cancel();
    await connection.close();

    final again = await connectTo(host, defaultPort, LoginRequest(method: AuthMethods.accessCode, secret: code),
        knownHosts: known, prompts: prompts);
    expect(prompts.newHostPrompts, 1); // 이미 신뢰한 Host
    await again.close();
  }, skip: skip, timeout: const Timeout(Duration(seconds: 40)));

  final password = Platform.environment['REMODESK_PASSWORD'];
  test('password login + remember device + device login (real C# Host)', () async {
    final known = MemoryKnownHosts();
    final devices = MemoryDevices();
    final c1 = await connectTo(host!, defaultPort,
        LoginRequest(method: AuthMethods.password, secret: password!, rememberDevice: true),
        knownHosts: known, devices: devices);
    expect(c1.method, AuthMethods.password);
    await c1.close();
    expect(devices.map.length, 1);

    await Future<void>.delayed(const Duration(milliseconds: 300));
    final c2 = await connectTo(host, defaultPort, const LoginRequest(method: AuthMethods.password, secret: ''),
        knownHosts: known, devices: devices);
    expect(c2.method, AuthMethods.device);
    await c2.close();
  }, skip: skip ?? (password == null ? 'REMODESK_PASSWORD 미설정' : null), timeout: const Timeout(Duration(seconds: 40)));
}
