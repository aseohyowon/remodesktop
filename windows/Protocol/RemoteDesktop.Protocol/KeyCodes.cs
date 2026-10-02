namespace RemoteDesktop.Protocol;

/// <summary>
/// 프로토콜의 키 이름(W3C KeyboardEvent.code)과 Windows 가상 키(VK) 변환표입니다.
/// 키 이름은 "글자"가 아니라 "물리적 키 위치"입니다. 예: Shift + KeyA = 'A'.
/// 한글은 Host의 IME가 조합하므로 Client는 키 위치만 보내면 됩니다. (한/영 = Lang1, 한자 = Lang2)
/// 참고: https://www.w3.org/TR/uievents-code/
/// </summary>
public static class KeyCodes
{
    private static readonly Dictionary<string, (ushort Vk, bool Extended)> ByCode = Build();
    private static readonly Dictionary<(ushort Vk, bool Extended), string> ByVk =
        ByCode.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static bool TryGetVirtualKey(string? code, out ushort virtualKey, out bool extended)
    {
        if (code is not null && ByCode.TryGetValue(code, out var key))
        {
            virtualKey = key.Vk;
            extended = key.Extended;
            return true;
        }

        virtualKey = 0;
        extended = false;
        return false;
    }

    /// <summary>
    /// Windows 키 이벤트(VK + extended 플래그)를 키 이름으로 바꿉니다.
    /// extended 플래그가 기대와 다르게 오는 경우(NumLock 꺼진 숫자패드 방향키 등)도 처리합니다.
    /// </summary>
    public static bool TryGetCode(int virtualKey, bool extended, out string code)
    {
        if (virtualKey is > 0 and <= ushort.MaxValue)
        {
            var vk = (ushort)virtualKey;
            if (ByVk.TryGetValue((vk, extended), out string? exact) || ByVk.TryGetValue((vk, !extended), out exact))
            {
                code = exact;
                return true;
            }
        }

        code = string.Empty;
        return false;
    }

    private static Dictionary<string, (ushort, bool)> Build()
    {
        var map = new Dictionary<string, (ushort, bool)>(StringComparer.Ordinal);

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            map[$"Key{letter}"] = (letter, false);
        }

        for (int digit = 0; digit <= 9; digit++)
        {
            map[$"Digit{digit}"] = ((ushort)('0' + digit), false);
            map[$"Numpad{digit}"] = ((ushort)(0x60 + digit), false);
        }

        for (int function = 1; function <= 24; function++)
        {
            map[$"F{function}"] = ((ushort)(0x70 + function - 1), false);
        }

        void Add(string code, ushort vk, bool extended = false) => map[code] = (vk, extended);

        // 편집/제어
        Add("Backspace", 0x08);
        Add("Tab", 0x09);
        Add("Enter", 0x0D);
        Add("NumpadEnter", 0x0D, true);
        Add("Pause", 0x13);
        Add("CapsLock", 0x14);
        Add("Escape", 0x1B);
        Add("Space", 0x20);
        Add("PrintScreen", 0x2C, true);
        Add("ScrollLock", 0x91);
        Add("NumLock", 0x90, true);
        Add("ContextMenu", 0x5D, true);

        // 이동 (extended 키)
        Add("PageUp", 0x21, true);
        Add("PageDown", 0x22, true);
        Add("End", 0x23, true);
        Add("Home", 0x24, true);
        Add("ArrowLeft", 0x25, true);
        Add("ArrowUp", 0x26, true);
        Add("ArrowRight", 0x27, true);
        Add("ArrowDown", 0x28, true);
        Add("Insert", 0x2D, true);
        Add("Delete", 0x2E, true);

        // 수정 키 (왼쪽/오른쪽 구분)
        Add("ShiftLeft", 0xA0);
        Add("ShiftRight", 0xA1);
        Add("ControlLeft", 0xA2);
        Add("ControlRight", 0xA3, true);
        Add("AltLeft", 0xA4);
        Add("AltRight", 0xA5, true);
        Add("MetaLeft", 0x5B, true);   // 왼쪽 Windows 키
        Add("MetaRight", 0x5C, true);  // 오른쪽 Windows 키

        // 숫자패드 연산자
        Add("NumpadMultiply", 0x6A);
        Add("NumpadAdd", 0x6B);
        Add("NumpadSubtract", 0x6D);
        Add("NumpadDecimal", 0x6E);
        Add("NumpadDivide", 0x6F, true);

        // 기호 (US 배열 기준 위치)
        Add("Semicolon", 0xBA);
        Add("Equal", 0xBB);
        Add("Comma", 0xBC);
        Add("Minus", 0xBD);
        Add("Period", 0xBE);
        Add("Slash", 0xBF);
        Add("Backquote", 0xC0);
        Add("BracketLeft", 0xDB);
        Add("Backslash", 0xDC);
        Add("BracketRight", 0xDD);
        Add("Quote", 0xDE);
        Add("IntlBackslash", 0xE2);

        // 한국어 키보드
        Add("Lang1", 0x15);  // 한/영 (VK_HANGUL)
        Add("Lang2", 0x19);  // 한자 (VK_HANJA)

        return map;
    }
}
