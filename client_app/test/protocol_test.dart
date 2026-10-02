import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/protocol/auth.dart';
import 'package:remodesktop_client/protocol/protocol.dart';

Uint8List _range(int start) => Uint8List.fromList(List.generate(32, (i) => start + i));

void main() {
  group('auth', () {
    test('access code normalization', () {
      expect(normalizeAccessCode('k7mpq-2xrta '), 'K7MPQ2XRTA');
    });

    // C# AuthProof.Compute로 만든 값과 같아야 Windows Host와 연결됩니다.
    test('proof matches C# implementation', () {
      expect(base64.encode(computeProof(AuthRole.client, deriveAccessCodeKey('k7mpq-2xrta'), _range(0), _range(32), _range(64))),
          '+sGhO+oce/K8SdCSIcU+oTKQhL6fMoYk5Hu5FQKV6uA=');
      expect(base64.encode(computeProof(AuthRole.host, deriveAccessCodeKey('K7MPQ2XRTA'), _range(0), _range(32), _range(64))),
          'VcGqC8qG/WWWUZ+wzsahA4Mfp4fICp8duVC/ZUlTDsQ=');
    });

    test('verify rejects wrong code', () {
      final a = deriveAccessCodeKey('AAAAA');
      final b = deriveAccessCodeKey('AAAAB');
      final proof = base64.encode(computeProof(AuthRole.client, a, _range(0), _range(32), _range(64)));
      expect(verifyProof(AuthRole.client, a, _range(0), _range(32), _range(64), proof), isTrue);
      expect(verifyProof(AuthRole.client, b, _range(0), _range(32), _range(64), proof), isFalse);
      expect(verifyProof(AuthRole.host, a, _range(0), _range(32), _range(64), proof), isFalse);
    });

    // RFC 7914 §11 PBKDF2-HMAC-SHA256 벡터 (C# 테스트와 동일)
    test('password key = PBKDF2-SHA256', () {
      expect(hexUpper(derivePasswordKey('passwd', utf8.encode('salt'), 1)),
          '55AC046E56E3089FEC1691C22544B605F94185216DDE0465E68B9D57C20DACBC');
      expect(hexUpper(derivePasswordKey('Password', utf8.encode('NaCl'), 80000)),
          '4DDCD8F60B98BE21830CEE5EF22701F9641A4418D04C0414AEFF08876B34AB56');
    });

    test('fingerprint format', () {
      expect(formatFingerprint([0xE2, 0x4C, 0x0A, 0x66]), 'E24C 0A66');
    });
  });

  group('framing', () {
    test('control message: type first, round trip', () {
      final bytes = encodeControl(mouseMoveMessage(0.5, 1.5));
      final json = utf8.decode(bytes.sublist(5));
      expect(json, startsWith('{"type":"mouse_move"'));
      final messages = FrameDecoder().add(bytes);
      expect(messages.single.control, {'type': 'mouse_move', 'x': 0.5, 'y': 1.0});
    });

    test('decoder handles arbitrary chunking', () {
      final header = ByteData(21)
        ..setUint32(0, 7)
        ..setInt64(4, 1234567890123)
        ..setInt32(12, 1920)
        ..setInt32(16, 1080)
        ..setUint8(20, codecJpeg);
      final video = encodeMessage(kindVideoFrame, [...header.buffer.asUint8List(), 0xFF, 0xD8, 0xFF, 0xD9]);
      final stream = [...encodeControl(keyDownMessage('KeyA')), ...video, ...encodeControl(byeMessage('x'))];

      final random = Random(1);
      for (var round = 0; round < 50; round++) {
        final decoder = FrameDecoder();
        final out = <ReceivedMessage>[];
        var i = 0;
        while (i < stream.length) {
          final n = min(1 + random.nextInt(7), stream.length - i);
          out.addAll(decoder.add(stream.sublist(i, i + n)));
          i += n;
        }
        expect(out.length, 3);
        expect(out[0].type, 'key_down');
        expect(out[1].video!.frameId, 7);
        expect(out[1].video!.captureTimeMs, 1234567890123);
        expect(out[1].video!.width, 1920);
        expect(out[1].video!.height, 1080);
        expect(out[1].video!.data, [0xFF, 0xD8, 0xFF, 0xD9]);
        expect(out[2].type, 'bye');
      }
    });

    test('rejects absurd length', () {
      expect(() => FrameDecoder().add([0x7F, 0xFF, 0xFF, 0xFF, 1]), throwsA(isA<ProtocolException>()));
    });

    test('unknown kind is ignored', () {
      final m = FrameDecoder().add(encodeMessage(99, [1, 2, 3])).single;
      expect(m.control, isNull);
      expect(m.video, isNull);
    });
  });
}
