// STEP 11 LAN 검색: 메시지 형식(C#과 동일), 가짜 응답기와 주고받기, 실제 Host(환경 변수가 있을 때)

import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/protocol/discovery.dart';

void main() {
  test('probe format matches C#', () {
    // C# DiscoveryTests.MessagesRoundTrip 과 같은 문자열
    expect(createProbe('ABC'), '{"type":"rd_discover","version":1,"nonce":"ABC"}');
  });

  test('reply validation', () {
    List<int> reply(Map<String, Object?> m) => utf8.encode(jsonEncode(m));
    final ok = {'type': 'rd_host', 'version': 1, 'nonce': 'N1', 'host_id': 'HOST-A', 'host_name': 'PC', 'port': 50505};
    expect(parseReply(reply(ok), 'N1', '192.168.0.5')!.address, '192.168.0.5');
    expect(parseReply(reply(ok), 'OTHER', '1.2.3.4'), isNull); // 다른 검색의 응답
    expect(parseReply(reply({...ok, 'port': 0}), 'N1', '1.2.3.4'), isNull);
    expect(parseReply(utf8.encode('garbage'), 'N1', '1.2.3.4'), isNull);
  });

  test('discovers fake responder over UDP', () async {
    final responder = await RawDatagramSocket.bind(InternetAddress.loopbackIPv4, 0);
    responder.listen((event) {
      final d = responder.receive();
      if (d == null) return;
      final probe = jsonDecode(utf8.decode(d.data)) as Map<String, dynamic>;
      responder.send(
          utf8.encode(jsonEncode({'type': 'rd_host', 'version': 1, 'nonce': probe['nonce'], 'host_id': 'HOST-FAKE01', 'host_name': 'FAKE-PC', 'port': 50505})),
          d.address,
          d.port);
    });

    final found = await discoverHosts(
      timeout: const Duration(milliseconds: 800),
      port: responder.port,
      targets: [InternetAddress.loopbackIPv4],
    );
    responder.close();
    expect(found.single.hostId, 'HOST-FAKE01');
    expect(found.single.address, '127.0.0.1');
  });

  final realHost = Platform.environment['REMODESK_HOST'];
  test('discovers real Windows Host by broadcast', () async {
    final found = await discoverHosts();
    expect(found, isNotEmpty);
  }, skip: realHost == null ? 'REMODESK_HOST 미설정 (실제 Host가 같은 네트워크에 있어야 함)' : null);
}
