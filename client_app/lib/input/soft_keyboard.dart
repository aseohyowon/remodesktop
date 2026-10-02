// 모바일 가상 키보드 입력 → 프로토콜 메시지 변환
//
// 화면에 보이지 않는 TextField에 항상 sentinel(공백 1개)을 넣어 둡니다.
// - 글자가 추가되면 → text_input (한글은 조합이 끝난 뒤 완성 글자만 보냄)
// - sentinel이 지워지면 → Backspace 키
// - 줄바꿈 → Enter 키
// 처리 후에는 다시 sentinel만 남깁니다.

const String keyboardSentinel = ' ';

sealed class KeyboardAction {
  const KeyboardAction();
}

class TypeText extends KeyboardAction {
  const TypeText(this.text);
  final String text;

  @override
  bool operator ==(Object other) => other is TypeText && other.text == text;

  @override
  int get hashCode => text.hashCode;

  @override
  String toString() => 'TypeText($text)';
}

class PressKey extends KeyboardAction {
  const PressKey(this.code);
  final String code;

  @override
  bool operator ==(Object other) => other is PressKey && other.code == code;

  @override
  int get hashCode => code.hashCode;

  @override
  String toString() => 'PressKey($code)';
}

/// 조합이 끝난 TextField 내용(text)을 보고 보낼 동작을 계산합니다.
List<KeyboardAction> diffKeyboardText(String text) {
  if (text == keyboardSentinel) {
    return const [];
  }
  if (!text.startsWith(keyboardSentinel)) {
    // sentinel이 지워짐 = Backspace. (드물게 자동완성이 전체를 바꾼 경우 남은 글자도 입력)
    return [const PressKey('Backspace'), ..._typed(text)];
  }
  return _typed(text.substring(keyboardSentinel.length));
}

List<KeyboardAction> _typed(String text) {
  final actions = <KeyboardAction>[];
  final lines = text.replaceAll('\r', '').split('\n');
  for (var i = 0; i < lines.length; i++) {
    if (lines[i].isNotEmpty) actions.add(TypeText(lines[i]));
    if (i < lines.length - 1) actions.add(const PressKey('Enter'));
  }
  return actions;
}

/// Ctrl/Alt 등 수정 키와 함께 누를 때는 문자 입력 대신 키 위치로 보내야 합니다. (Ctrl+C 등)
String? characterToKeyCode(String character) {
  if (character.length != 1) return null;
  final c = character.toLowerCase().codeUnitAt(0);
  if (c >= 0x61 && c <= 0x7A) return 'Key${String.fromCharCode(c).toUpperCase()}';
  if (c >= 0x30 && c <= 0x39) return 'Digit${String.fromCharCode(c)}';
  return const {
    ' ': 'Space',
    '-': 'Minus',
    '=': 'Equal',
    '[': 'BracketLeft',
    ']': 'BracketRight',
    '\\': 'Backslash',
    ';': 'Semicolon',
    "'": 'Quote',
    '`': 'Backquote',
    ',': 'Comma',
    '.': 'Period',
    '/': 'Slash',
  }[character];
}
