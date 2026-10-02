// LAN 자동 검색 (STEP 11). Windows의 LanDiscovery.cs와 같은 메시지입니다.
//   요청: {"type":"rd_discover","version":1,"nonce":"..."}  → 255.255.255.255:50506 (UDP 브로드캐스트)
//   응답: {"type":"rd_host","version":1,"nonce":"...","host_id":"HOST-...","host_name":"PC","port":50505}
// 검색은 편의 기능일 뿐 인증이 아닙니다. 연결할 때 평소처럼 TLS + 인증을 거칩니다.
//
// iOS: 브로드캐스트를 보내려면 Apple의 Multicast Networking 권한(com.apple.developer.networking.multicast)이
//      필요합니다. 권한이 없으면 검색이 실패하므로 IP를 직접 입력해 추가하세요.

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

const int discoveryPort = 50506;

class FoundHost {
  FoundHost(this.hostId, this.hostName, this.address, this.port, [this.mac = const []]);
  final String hostId;
  final String hostName;
  final String address;
  final int port;

  /// Wake-on-LAN용 MAC 주소
  final List<String> mac;
}

String createProbe(String nonce) => jsonEncode({'type': 'rd_discover', 'version': 1, 'nonce': nonce});

/// 응답 검사: 우리가 보낸 nonce가 있고 형식이 맞아야 함
FoundHost? parseReply(List<int> data, String nonce, String fromAddress) {
  if (data.length > 1024) return null;
  try {
    final m = jsonDecode(utf8.decode(data));
    if (m is! Map<String, dynamic> || m['type'] != 'rd_host' || m['version'] != 1 || m['nonce'] != nonce) return null;
    final port = (m['port'] as num?)?.toInt() ?? 0;
    final hostId = m['host_id'] as String? ?? '';
    if (port <= 0 || port > 65535 || hostId.isEmpty) return null;
    final name = (m['host_name'] as String? ?? '-').replaceAll(RegExp(r'[\x00-\x1F]'), '');
    final mac = ((m['mac'] as List?) ?? []).whereType<String>().where((x) => x.length <= 17).take(8).toList();
    return FoundHost(hostId, name.length > 64 ? name.substring(0, 64) : name, fromAddress, port, mac);
  } catch (_) {
    return null;
  }
}

/// 같은 네트워크의 Host를 찾습니다. targets: 브로드캐스트 대신 물어볼 주소 (테스트용)
Future<List<FoundHost>> discoverHosts({
  Duration timeout = const Duration(seconds: 2),
  int port = discoveryPort,
  List<InternetAddress>? targets,
}) async {
  final random = Random.secure();
  final nonce = List.generate(8, (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0')).join().toUpperCase();
  final socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
  socket.broadcastEnabled = true;

  final found = <String, FoundHost>{};
  final subscription = socket.listen((event) {
    if (event != RawSocketEvent.read) return;
    final datagram = socket.receive();
    if (datagram == null) return;
    final host = parseReply(datagram.data, nonce, datagram.address.address);
    if (host != null) found.putIfAbsent(host.hostId, () => host);
  });

  final probe = utf8.encode(createProbe(nonce));
  for (final target in targets ?? [InternetAddress('255.255.255.255')]) {
    for (var attempt = 0; attempt < 5; attempt++) {
      try {
        if (socket.send(probe, target, port) > 0) break; // 0이면 버퍼가 잠깐 차 있음 → 다시 시도
      } catch (_) {
        break; // 브로드캐스트 권한이 없는 플랫폼 등
      }
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }
  }

  await Future<void>.delayed(timeout);
  await subscription.cancel();
  socket.close();
  return found.values.toList()..sort((a, b) => a.hostName.compareTo(b.hostName));
}
