// 등록된 PC 목록과 신뢰한 Host 인증서 지문을 기기에 저장합니다.
// 접속 코드는 Host를 다시 시작할 때마다 바뀌고 민감 정보이므로 저장하지 않습니다.

import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../protocol/connection.dart';
import '../protocol/protocol.dart';

class SavedHost {
  SavedHost({required this.id, required this.name, required this.address, this.port = defaultPort, this.hostId});

  final String id;
  final String name;
  final String address;
  final int port;

  /// 처음 연결에 성공하면 Host가 알려 준 Host ID (예: HOST-939E16)
  /// 인터넷 PC는 이 값으로 연결합니다(address는 빈 문자열).
  final String? hostId;

  bool get isInternet => address.isEmpty && (hostId?.isNotEmpty ?? false);

  SavedHost copyWith({String? name, String? address, int? port, String? hostId}) => SavedHost(
        id: id,
        name: name ?? this.name,
        address: address ?? this.address,
        port: port ?? this.port,
        hostId: hostId ?? this.hostId,
      );

  Map<String, Object?> toJson() => {'id': id, 'name': name, 'address': address, 'port': port, 'host_id': hostId};

  static SavedHost fromJson(Map<String, dynamic> json) => SavedHost(
        id: json['id'] as String,
        name: json['name'] as String,
        address: json['address'] as String,
        port: (json['port'] as num?)?.toInt() ?? defaultPort,
        hostId: json['host_id'] as String?,
      );
}

class HostStore {
  static const _hostsKey = 'hosts';
  static const _signalingKey = 'signaling_server';

  /// 시그널링 서버 (예: wss://signal.example.com/ws). 없으면 null
  Future<Uri?> loadSignaling() async {
    final prefs = await SharedPreferences.getInstance();
    final value = prefs.getString(_signalingKey);
    final uri = value == null ? null : Uri.tryParse(value);
    return (uri != null && (uri.scheme == 'ws' || uri.scheme == 'wss')) ? uri : null;
  }

  Future<void> saveSignaling(String? value) async {
    final prefs = await SharedPreferences.getInstance();
    if (value == null || value.trim().isEmpty) {
      await prefs.remove(_signalingKey);
    } else {
      await prefs.setString(_signalingKey, value.trim());
    }
  }

  Future<List<SavedHost>> load() async {
    final prefs = await SharedPreferences.getInstance();
    final raw = prefs.getString(_hostsKey);
    if (raw == null) {
      return [];
    }
    try {
      return (jsonDecode(raw) as List).map((e) => SavedHost.fromJson(e as Map<String, dynamic>)).toList();
    } catch (_) {
      return [];
    }
  }

  Future<void> saveAll(List<SavedHost> hosts) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_hostsKey, jsonEncode(hosts.map((h) => h.toJson()).toList()));
  }
}

/// TOFU 지문 저장소
class PrefsKnownHosts implements KnownHosts {
  static const _key = 'known_hosts';

  Future<Map<String, String>> _load() async {
    final prefs = await SharedPreferences.getInstance();
    final raw = prefs.getString(_key);
    if (raw == null) {
      return {};
    }
    try {
      return Map<String, String>.from(jsonDecode(raw) as Map);
    } catch (_) {
      return {};
    }
  }

  @override
  Future<HostTrust> check(String hostId, String fingerprintHex) async {
    final saved = (await _load())[hostId];
    if (saved == null) {
      return HostTrust.unknown;
    }
    return saved.toUpperCase() == fingerprintHex.toUpperCase() ? HostTrust.trusted : HostTrust.mismatch;
  }

  @override
  Future<void> save(String hostId, String fingerprintHex) async {
    final hosts = await _load()
      ..[hostId] = fingerprintHex;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_key, jsonEncode(hosts));
  }

  Future<void> forget(String hostId) async {
    final hosts = await _load()
      ..remove(hostId);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_key, jsonEncode(hosts));
  }
}

/// 신뢰된 장치 비밀키: iOS/macOS 키체인, Android Keystore로 암호화된 보안 저장소에 보관합니다.
class SecureDeviceCredentials implements DeviceCredentials {
  static const _storage = FlutterSecureStorage();
  static String _key(String hostId) => 'device:$hostId';

  @override
  Future<DeviceCredential?> get(String hostId) async {
    final raw = await _storage.read(key: _key(hostId));
    if (raw == null) return null;
    try {
      final json = jsonDecode(raw) as Map<String, dynamic>;
      return DeviceCredential(json['device_id'] as String, base64.decode(json['secret'] as String));
    } catch (_) {
      return null;
    }
  }

  @override
  Future<void> save(String hostId, DeviceCredential credential) => _storage.write(
        key: _key(hostId),
        value: jsonEncode({'device_id': credential.deviceId, 'secret': base64.encode(credential.secret)}),
      );

  @override
  Future<void> remove(String hostId) => _storage.delete(key: _key(hostId));

  Future<bool> has(String hostId) async => await _storage.containsKey(key: _key(hostId));
}
