// Wake-on-LAN (STEP 12). Windows의 WakeOnLan.cs와 같은 규칙입니다.
// 매직 패킷 = FF×6 + MAC×16 (102바이트) → UDP 9번 브로드캐스트. 같은 네트워크에서만 동작합니다.

import 'dart:io';
import 'dart:typed_data';

const int wakeOnLanPort = 9;

/// "AA-BB-CC-DD-EE-FF", "aa:bb:...", "AABBCCDDEEFF" → 6바이트. 잘못되면 null
Uint8List? parseMac(String? mac) {
  if (mac == null || mac.length > 17) return null;
  final hex = mac.replaceAll(RegExp(r'[^0-9A-Fa-f]'), '');
  if (hex.length != 12) return null;
  final bytes = Uint8List.fromList(List.generate(6, (i) => int.parse(hex.substring(i * 2, i * 2 + 2), radix: 16)));
  if (bytes.every((b) => b == 0) || bytes.every((b) => b == 0xFF)) return null;
  return bytes;
}

Uint8List magicPacket(Uint8List mac) {
  final packet = Uint8List(102);
  packet.fillRange(0, 6, 0xFF);
  for (var i = 0; i < 16; i++) {
    packet.setRange(6 + i * 6, 12 + i * 6, mac);
  }
  return packet;
}

/// 매직 패킷을 보냅니다(잃어버리기 쉬운 UDP라 세 번). 보낸 MAC 수를 반환합니다.
Future<int> wakeOnLan(List<String> macs, {List<InternetAddress>? targets, int port = wakeOnLanPort}) async {
  final socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
  socket.broadcastEnabled = true;
  var sent = 0;
  for (final mac in macs) {
    final bytes = parseMac(mac);
    if (bytes == null) continue;
    final packet = magicPacket(bytes);
    for (final target in targets ?? [InternetAddress('255.255.255.255')]) {
      for (var i = 0; i < 3; i++) {
        await _send(socket, packet, target, port);
        await Future<void>.delayed(const Duration(milliseconds: 20));
      }
    }
    sent++;
  }
  socket.close();
  return sent;
}

/// send()는 버퍼가 잠깐 차 있으면 0(보내지 못함)을 돌려주므로 몇 번 다시 시도합니다.
Future<void> _send(RawDatagramSocket socket, List<int> data, InternetAddress target, int port) async {
  for (var attempt = 0; attempt < 5; attempt++) {
    try {
      if (socket.send(data, target, port) > 0) return;
    } catch (_) {
      return; // 브로드캐스트 권한이 없는 플랫폼 등
    }
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
}
