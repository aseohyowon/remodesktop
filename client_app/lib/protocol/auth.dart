// 상호 인증 (docs/protocol.md 5장). C#의 AuthProof.cs와 같은 계산입니다.

import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

const int nonceBytes = 32;
const int keyBytes = 32;

enum AuthRole { client, host }

/// 인증 방식 (auth_challenge.auth_methods)
class AuthMethods {
  static const accessCode = 'access_code';
  static const password = 'password';
  static const device = 'device';
}

/// auth_result.error_code
class AuthErrorCodes {
  static const invalidCredentials = 'invalid_credentials';
  static const totpRequired = 'totp_required';
  static const deviceRevoked = 'device_revoked';
  static const denied = 'denied';
  static const busy = 'busy';
}

/// 영문/숫자만 남기고 대문자로. "k7mpq-2xrta" → "K7MPQ2XRTA"
String normalizeAccessCode(String code) => code.toUpperCase().replaceAll(RegExp('[^A-Z0-9]'), '');

/// access_code 방식 키
Uint8List deriveAccessCodeKey(String accessCode) =>
    Uint8List.fromList(sha256.convert(utf8.encode('remodesktop-access-code-v1:${normalizeAccessCode(accessCode)}')).bytes);

/// password 방식 키: PBKDF2-HMAC-SHA256, 32바이트 (C# Rfc2898DeriveBytes.Pbkdf2와 동일)
Uint8List derivePasswordKey(String password, List<int> salt, int iterations) {
  final hmac = Hmac(sha256, utf8.encode(password));
  // 블록 1개(32바이트)면 충분: U1 = HMAC(P, S || INT(1))
  var u = hmac.convert([...salt, 0, 0, 0, 1]).bytes;
  final result = Uint8List.fromList(u);
  for (var i = 1; i < iterations; i++) {
    u = hmac.convert(u).bytes;
    for (var j = 0; j < result.length; j++) {
      result[j] ^= u[j];
    }
  }
  return result;
}

Uint8List computeProof(
  AuthRole role,
  List<int> key,
  List<int> clientNonce,
  List<int> serverNonce,
  List<int> channelBinding,
) {
  final label = utf8.encode(role == AuthRole.client ? 'remodesktop-auth-v1:client' : 'remodesktop-auth-v1:host');
  final data = <int>[...label, ...clientNonce, ...serverNonce, ...channelBinding];
  return Uint8List.fromList(Hmac(sha256, key).convert(data).bytes);
}

bool verifyProof(
  AuthRole role,
  List<int> key,
  List<int> clientNonce,
  List<int> serverNonce,
  List<int> channelBinding,
  String? proofBase64,
) {
  if (proofBase64 == null) {
    return false;
  }
  final List<int> proof;
  try {
    proof = base64.decode(proofBase64);
  } on FormatException {
    return false;
  }
  return _constantTimeEquals(computeProof(role, key, clientNonce, serverNonce, channelBinding), proof);
}

Uint8List randomBytes(int length) {
  final random = Random.secure();
  return Uint8List.fromList(List<int>.generate(length, (_) => random.nextInt(256)));
}

Uint8List createNonce() => randomBytes(nonceBytes);

Uint8List? decodeFixed(Object? value, int length) {
  if (value is! String) {
    return null;
  }
  try {
    final bytes = base64.decode(value);
    return bytes.length == length ? bytes : null;
  } on FormatException {
    return null;
  }
}

Uint8List? decodeNonce(Object? value) => decodeFixed(value, nonceBytes);

Uint8List sha256Bytes(List<int> data) => Uint8List.fromList(sha256.convert(data).bytes);

String hexUpper(List<int> bytes) => bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join().toUpperCase();

/// "E24C 0A66 8A1B ..." (Host 콘솔 표시와 같은 형식)
String formatFingerprint(List<int> certificateHash) {
  final hex = hexUpper(certificateHash);
  final groups = <String>[];
  for (var i = 0; i < hex.length; i += 4) {
    groups.add(hex.substring(i, i + 4));
  }
  return groups.join(' ');
}

bool _constantTimeEquals(List<int> a, List<int> b) {
  if (a.length != b.length) {
    return false;
  }
  var diff = 0;
  for (var i = 0; i < a.length; i++) {
    diff |= a[i] ^ b[i];
  }
  return diff == 0;
}
