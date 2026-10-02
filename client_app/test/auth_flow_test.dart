// STEP 6 인증 흐름 (Dart Client ↔ 가짜 Host)

import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/protocol/auth.dart';
import 'package:remodesktop_client/protocol/connection.dart';

import 'support/fake_host.dart';

const _code = LoginRequest(method: AuthMethods.accessCode, secret: 'testcode-42');

void main() {
  late FakeHost host;

  Future<void> start(FakeHost h) async {
    host = h;
    await host.start();
  }

  tearDown(() => host.stop());

  test('access code login + TOFU prompt only once', () async {
    await start(FakeHost());
    final known = MemoryKnownHosts();
    final prompts = TestPrompts();

    final c1 = await connectTo('127.0.0.1', host.port, _code, knownHosts: known, prompts: prompts);
    expect(c1.hostId, 'HOST-FAKE01');
    expect(c1.screenWidth, 1920);
    await c1.close();

    final c2 = await connectTo('127.0.0.1', host.port, _code, knownHosts: known, prompts: prompts);
    await c2.close();
    expect(prompts.newHostPrompts, 1);
  });

  test('declining fingerprint aborts before sending proof', () async {
    await start(FakeHost());
    await expectLater(
      connectTo('127.0.0.1', host.port, _code, prompts: TestPrompts(trustNewHost: false)),
      throwsA(isA<ConnectionFailed>()),
    );
    expect(host.lastAuthResponse, isNull);
  });

  test('wrong code', () async {
    await start(FakeHost());
    await expectLater(
      connectTo('127.0.0.1', host.port, const LoginRequest(method: AuthMethods.accessCode, secret: 'nope')),
      throwsA(isA<ConnectionFailed>().having((e) => e.errorCode, 'code', AuthErrorCodes.invalidCredentials)),
    );
  });

  test('password login (PBKDF2 in isolate)', () async {
    await start(FakeHost(password: 'correct horse battery'));
    final c = await connectTo('127.0.0.1', host.port,
        const LoginRequest(method: AuthMethods.password, secret: 'correct horse battery'));
    expect(c.method, AuthMethods.password);
    await c.close();

    await expectLater(
      connectTo('127.0.0.1', host.port, const LoginRequest(method: AuthMethods.password, secret: 'wrong')),
      throwsA(isA<ConnectionFailed>()),
    );
  });

  test('password not offered → clear message', () async {
    await start(FakeHost());
    await expectLater(
      connectTo('127.0.0.1', host.port, const LoginRequest(method: AuthMethods.password, secret: 'x')),
      throwsA(isA<ConnectionFailed>().having((e) => e.message, 'message', contains('비밀번호가 설정되어 있지 않습니다'))),
    );
  });

  test('remember device → next login without secret → revoked', () async {
    await start(FakeHost());
    final devices = MemoryDevices();

    final c1 = await connectTo('127.0.0.1', host.port,
        const LoginRequest(method: AuthMethods.accessCode, secret: 'TESTCODE42', rememberDevice: true),
        devices: devices);
    await c1.close();
    expect(devices.map['HOST-FAKE01'], isNotNull);

    final c2 = await connectTo('127.0.0.1', host.port, const LoginRequest(method: AuthMethods.accessCode, secret: ''),
        devices: devices);
    expect(c2.method, AuthMethods.device);
    await c2.close();

    host.devices.clear(); // Host에서 등록 해제
    await expectLater(
      connectTo('127.0.0.1', host.port, const LoginRequest(method: AuthMethods.accessCode, secret: ''), devices: devices),
      throwsA(isA<ConnectionFailed>().having((e) => e.errorCode, 'code', AuthErrorCodes.deviceRevoked)),
    );
    expect(devices.map, isEmpty);
  });

  test('2FA required', () async {
    await start(FakeHost(totpCode: '123456'));

    await expectLater(
      connectTo('127.0.0.1', host.port, _code, prompts: TestPrompts(totp: '000000')),
      throwsA(isA<ConnectionFailed>().having((e) => e.errorCode, 'code', AuthErrorCodes.totpRequired)),
    );

    final prompts = TestPrompts(totp: '123456');
    final c = await connectTo('127.0.0.1', host.port, _code, prompts: prompts);
    expect(prompts.totpPrompts, 1);
    expect(host.lastAuthResponse!['totp'], '123456');
    await c.close();
  });
}
