// STEP 12 Wake-on-LAN: MAC 형식, 매직 패킷(C#과 같은 규칙), UDP 전송

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/protocol/discovery.dart';
import 'package:remodesktop_client/protocol/protocol.dart';
import 'package:remodesktop_client/protocol/wake_on_lan.dart';
import 'dart:convert';

void main() {
  test('parse MAC', () {
    expect(parseMac('AA-BB-CC-DD-EE-FF'), [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    expect(parseMac('aa:bb:cc:dd:ee:ff'), isNotNull);
    for (final bad in ['00-00-00-00-00-00', 'FF-FF-FF-FF-FF-FF', 'AA-BB', 'zz-bb-cc-dd-ee-ff', null]) {
      expect(parseMac(bad), isNull, reason: '$bad');
    }
  });

  test('magic packet', () {
    final mac = parseMac('01-23-45-67-89-AB')!;
    final p = magicPacket(mac);
    expect(p.length, 102);
    expect(p.sublist(0, 6), List.filled(6, 0xFF));
    expect(p.sublist(96, 102), mac);
  });

  test('sends over UDP', () async {
    final listener = await RawDatagramSocket.bind(InternetAddress.loopbackIPv4, 0);
    final received = <int>[];
    listener.listen((e) {
      final d = listener.receive();
      if (d != null) received.add(d.data.length);
    });
    final sent = await wakeOnLan(['01-23-45-67-89-AB'], targets: [InternetAddress.loopbackIPv4], port: listener.port);
    await Future<void>.delayed(const Duration(milliseconds: 300));
    listener.close();
    expect(sent, 1);
    expect(received, [102, 102, 102]);
  });

  test('discovery reply carries MAC', () {
    final data = utf8.encode(jsonEncode({
      'type': 'rd_host', 'version': 1, 'nonce': 'N', 'host_id': 'HOST-A', 'host_name': 'PC', 'port': 50505,
      'mac': ['AA-BB-CC-DD-EE-FF'],
    }));
    expect(parseReply(data, 'N', '192.168.0.2')!.mac, ['AA-BB-CC-DD-EE-FF']);
  });

  test('set_resolution message', () {
    expect(utf8.decode(encodeControl(setResolutionMessage(1280, 720)).sublist(5)),
        '{"type":"set_resolution","width":1280,"height":720}');
  });
}
