using System.Runtime.InteropServices;

namespace RemoteDesktop.Media;

/// <summary>
/// Windows ICodecAPI (strmif.h). Vortice에 없어서 직접 선언합니다.
/// 인코더 설정(비트레이트, 저지연, 키프레임 강제 등)을 바꿀 때 사용합니다.
/// 메서드 순서는 vtable 순서와 같아야 합니다.
/// </summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);

    [PreserveSig] int IsModifiable(ref Guid api);

    [PreserveSig] int GetParameterRange(ref Guid api, out object valueMin, out object valueMax, out object steppingDelta);

    [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint valuesCount);

    [PreserveSig] int GetDefaultValue(ref Guid api, out object value);

    [PreserveSig] int GetValue(ref Guid api, out object value);

    [PreserveSig] int SetValue(ref Guid api, ref object value);
}

internal static class CodecApiKeys
{
    public static Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    public const uint RateControlCbr = 0;
}

internal static class CodecApiExtensions
{
    public static bool TrySet(this ICodecAPI api, Guid key, uint value)
    {
        object boxed = value;
        return api.SetValue(ref key, ref boxed) >= 0;
    }

    public static bool TrySet(this ICodecAPI api, Guid key, bool value)
    {
        object boxed = value;
        return api.SetValue(ref key, ref boxed) >= 0;
    }
}
