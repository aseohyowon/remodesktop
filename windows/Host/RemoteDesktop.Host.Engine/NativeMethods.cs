using System.Runtime.InteropServices;

namespace RemoteDesktop.Host.Engine;

public static class NativeMethods
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;

    public static void EnablePerMonitorDpiAwareness()
    {
        // Windows 10 1703 이상. 실패해도(이미 설정됨 등) 계속 진행합니다.
        SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
    }

    /// <summary>현재 마우스 커서를 캡처 이미지 위에 그립니다. (GDI 캡처에는 커서가 포함되지 않음)</summary>
    public static void DrawCursor(IntPtr deviceContext, int originX, int originY)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == IntPtr.Zero)
        {
            return;
        }

        int hotspotX = 0;
        int hotspotY = 0;
        if (GetIconInfo(info.Cursor, out IconInfo iconInfo))
        {
            hotspotX = iconInfo.HotspotX;
            hotspotY = iconInfo.HotspotY;

            if (iconInfo.MaskBitmap != IntPtr.Zero)
            {
                DeleteObject(iconInfo.MaskBitmap);
            }

            if (iconInfo.ColorBitmap != IntPtr.Zero)
            {
                DeleteObject(iconInfo.ColorBitmap);
            }
        }

        DrawIconEx(
            deviceContext,
            info.ScreenPosition.X - originX - hotspotX,
            info.ScreenPosition.Y - originY - hotspotY,
            info.Cursor,
            0,
            0,
            0,
            IntPtr.Zero,
            DiNormal);
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr deviceContext,
        int x,
        int y,
        IntPtr icon,
        int width,
        int height,
        int frameIndex,
        IntPtr flickerFreeBrush,
        int flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }
}
