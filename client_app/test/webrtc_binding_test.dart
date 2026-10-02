import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/protocol/auth.dart';
import 'package:remodesktop_client/protocol/webrtc_transport.dart';

void main() {
  const offer = 'v=0\r\na=fingerprint:sha-256 aa:bb:cc\r\n';
  const answer = 'v=0\r\na=fingerprint:sha-256 11:22:33\r\n';

  // C# WebRtcBindingTests와 같은 입력 → 같은 값이어야 함
  test('binding vector matches C#', () {
    expect(hexUpper(webRtcChannelBinding(offer, answer)),
        '258C072E6C22600CC44BBCA029B82DE62BFF054609541C029E3A1E14762A54C6');
  });

  test('order and fingerprint matter', () {
    final real = hexUpper(webRtcChannelBinding(offer, answer));
    expect(hexUpper(webRtcChannelBinding(answer, offer)), isNot(real));
    expect(hexUpper(webRtcChannelBinding(offer.replaceAll('aa', 'ab'), answer)), isNot(real));
  });

  test('status uri', () {
    expect(statusUri(Uri.parse('wss://signal.example.com/ws'), ['HOST-A', 'HOST-B']).toString(),
        'https://signal.example.com/api/status?ids=HOST-A%2CHOST-B');
  });
}
