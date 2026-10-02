// Flutter 물리 키 → 프로토콜 키 이름(W3C KeyboardEvent.code).
// Windows 쪽 변환표: windows/Protocol/RemoteDesktop.Protocol/KeyCodes.cs

import 'package:flutter/services.dart';

final Map<PhysicalKeyboardKey, String> _codes = {
  PhysicalKeyboardKey.keyA: 'KeyA', PhysicalKeyboardKey.keyB: 'KeyB', PhysicalKeyboardKey.keyC: 'KeyC',
  PhysicalKeyboardKey.keyD: 'KeyD', PhysicalKeyboardKey.keyE: 'KeyE', PhysicalKeyboardKey.keyF: 'KeyF',
  PhysicalKeyboardKey.keyG: 'KeyG', PhysicalKeyboardKey.keyH: 'KeyH', PhysicalKeyboardKey.keyI: 'KeyI',
  PhysicalKeyboardKey.keyJ: 'KeyJ', PhysicalKeyboardKey.keyK: 'KeyK', PhysicalKeyboardKey.keyL: 'KeyL',
  PhysicalKeyboardKey.keyM: 'KeyM', PhysicalKeyboardKey.keyN: 'KeyN', PhysicalKeyboardKey.keyO: 'KeyO',
  PhysicalKeyboardKey.keyP: 'KeyP', PhysicalKeyboardKey.keyQ: 'KeyQ', PhysicalKeyboardKey.keyR: 'KeyR',
  PhysicalKeyboardKey.keyS: 'KeyS', PhysicalKeyboardKey.keyT: 'KeyT', PhysicalKeyboardKey.keyU: 'KeyU',
  PhysicalKeyboardKey.keyV: 'KeyV', PhysicalKeyboardKey.keyW: 'KeyW', PhysicalKeyboardKey.keyX: 'KeyX',
  PhysicalKeyboardKey.keyY: 'KeyY', PhysicalKeyboardKey.keyZ: 'KeyZ',
  PhysicalKeyboardKey.digit1: 'Digit1', PhysicalKeyboardKey.digit2: 'Digit2', PhysicalKeyboardKey.digit3: 'Digit3',
  PhysicalKeyboardKey.digit4: 'Digit4', PhysicalKeyboardKey.digit5: 'Digit5', PhysicalKeyboardKey.digit6: 'Digit6',
  PhysicalKeyboardKey.digit7: 'Digit7', PhysicalKeyboardKey.digit8: 'Digit8', PhysicalKeyboardKey.digit9: 'Digit9',
  PhysicalKeyboardKey.digit0: 'Digit0',
  PhysicalKeyboardKey.f1: 'F1', PhysicalKeyboardKey.f2: 'F2', PhysicalKeyboardKey.f3: 'F3', PhysicalKeyboardKey.f4: 'F4',
  PhysicalKeyboardKey.f5: 'F5', PhysicalKeyboardKey.f6: 'F6', PhysicalKeyboardKey.f7: 'F7', PhysicalKeyboardKey.f8: 'F8',
  PhysicalKeyboardKey.f9: 'F9', PhysicalKeyboardKey.f10: 'F10', PhysicalKeyboardKey.f11: 'F11', PhysicalKeyboardKey.f12: 'F12',
  PhysicalKeyboardKey.enter: 'Enter', PhysicalKeyboardKey.escape: 'Escape', PhysicalKeyboardKey.backspace: 'Backspace',
  PhysicalKeyboardKey.tab: 'Tab', PhysicalKeyboardKey.space: 'Space', PhysicalKeyboardKey.minus: 'Minus',
  PhysicalKeyboardKey.equal: 'Equal', PhysicalKeyboardKey.bracketLeft: 'BracketLeft',
  PhysicalKeyboardKey.bracketRight: 'BracketRight', PhysicalKeyboardKey.backslash: 'Backslash',
  PhysicalKeyboardKey.semicolon: 'Semicolon', PhysicalKeyboardKey.quote: 'Quote', PhysicalKeyboardKey.backquote: 'Backquote',
  PhysicalKeyboardKey.comma: 'Comma', PhysicalKeyboardKey.period: 'Period', PhysicalKeyboardKey.slash: 'Slash',
  PhysicalKeyboardKey.capsLock: 'CapsLock', PhysicalKeyboardKey.printScreen: 'PrintScreen',
  PhysicalKeyboardKey.scrollLock: 'ScrollLock', PhysicalKeyboardKey.pause: 'Pause', PhysicalKeyboardKey.insert: 'Insert',
  PhysicalKeyboardKey.home: 'Home', PhysicalKeyboardKey.pageUp: 'PageUp', PhysicalKeyboardKey.delete: 'Delete',
  PhysicalKeyboardKey.end: 'End', PhysicalKeyboardKey.pageDown: 'PageDown',
  PhysicalKeyboardKey.arrowRight: 'ArrowRight', PhysicalKeyboardKey.arrowLeft: 'ArrowLeft',
  PhysicalKeyboardKey.arrowDown: 'ArrowDown', PhysicalKeyboardKey.arrowUp: 'ArrowUp',
  PhysicalKeyboardKey.numLock: 'NumLock', PhysicalKeyboardKey.numpadDivide: 'NumpadDivide',
  PhysicalKeyboardKey.numpadMultiply: 'NumpadMultiply', PhysicalKeyboardKey.numpadSubtract: 'NumpadSubtract',
  PhysicalKeyboardKey.numpadAdd: 'NumpadAdd', PhysicalKeyboardKey.numpadEnter: 'NumpadEnter',
  PhysicalKeyboardKey.numpad1: 'Numpad1', PhysicalKeyboardKey.numpad2: 'Numpad2', PhysicalKeyboardKey.numpad3: 'Numpad3',
  PhysicalKeyboardKey.numpad4: 'Numpad4', PhysicalKeyboardKey.numpad5: 'Numpad5', PhysicalKeyboardKey.numpad6: 'Numpad6',
  PhysicalKeyboardKey.numpad7: 'Numpad7', PhysicalKeyboardKey.numpad8: 'Numpad8', PhysicalKeyboardKey.numpad9: 'Numpad9',
  PhysicalKeyboardKey.numpad0: 'Numpad0', PhysicalKeyboardKey.numpadDecimal: 'NumpadDecimal',
  PhysicalKeyboardKey.intlBackslash: 'IntlBackslash', PhysicalKeyboardKey.contextMenu: 'ContextMenu',
  PhysicalKeyboardKey.controlLeft: 'ControlLeft', PhysicalKeyboardKey.controlRight: 'ControlRight',
  PhysicalKeyboardKey.shiftLeft: 'ShiftLeft', PhysicalKeyboardKey.shiftRight: 'ShiftRight',
  PhysicalKeyboardKey.altLeft: 'AltLeft', PhysicalKeyboardKey.altRight: 'AltRight',
  PhysicalKeyboardKey.metaLeft: 'MetaLeft', PhysicalKeyboardKey.metaRight: 'MetaRight',
  PhysicalKeyboardKey.lang1: 'Lang1', PhysicalKeyboardKey.lang2: 'Lang2',
};

/// 키 이름을 반환합니다. 모르는 키는 null.
/// [commandAsControl]: Mac의 ⌘(Command)를 Windows Ctrl로 보냅니다. (⌘C → Ctrl+C)
String? protocolKeyCode(PhysicalKeyboardKey key, {bool commandAsControl = false}) {
  if (commandAsControl) {
    if (key == PhysicalKeyboardKey.metaLeft) {
      return 'ControlLeft';
    }
    if (key == PhysicalKeyboardKey.metaRight) {
      return 'ControlRight';
    }
  }
  return _codes[key];
}
