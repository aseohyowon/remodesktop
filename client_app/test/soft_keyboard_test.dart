import 'package:flutter_test/flutter_test.dart';
import 'package:remodesktop_client/input/soft_keyboard.dart';

void main() {
  test('typed text', () {
    expect(diffKeyboardText('${keyboardSentinel}abc'), [const TypeText('abc')]);
    expect(diffKeyboardText('$keyboardSentinel안녕'), [const TypeText('안녕')]);
  });

  test('nothing changed', () {
    expect(diffKeyboardText(keyboardSentinel), isEmpty);
  });

  test('backspace deletes sentinel', () {
    expect(diffKeyboardText(''), [const PressKey('Backspace')]);
  });

  test('newline → Enter', () {
    expect(diffKeyboardText('$keyboardSentinel\n'), [const PressKey('Enter')]);
    expect(diffKeyboardText('${keyboardSentinel}ab\ncd'),
        [const TypeText('ab'), const PressKey('Enter'), const TypeText('cd')]);
  });

  test('character to key code', () {
    expect(characterToKeyCode('c'), 'KeyC');
    expect(characterToKeyCode('V'), 'KeyV');
    expect(characterToKeyCode('7'), 'Digit7');
    expect(characterToKeyCode('/'), 'Slash');
    expect(characterToKeyCode('한'), isNull);
  });
}
