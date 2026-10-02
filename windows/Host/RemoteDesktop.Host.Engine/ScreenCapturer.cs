using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 모니터 하나를 GDI(BitBlt)로 반복 캡처합니다. 같은 Bitmap을 재사용합니다.
/// STEP 8에서 Desktop Duplication / Windows Graphics Capture로 교체할 예정이므로
/// 사용하는 쪽은 Capture()와 Frame만 사용합니다.
/// </summary>
public sealed class ScreenCapturer : IScreenCapturer
{
    private readonly Rectangle _bounds;
    private readonly Bitmap _frame;
    private byte[] _previousPixels;
    private byte[] _currentPixels;
    private bool _hasPrevious;

    public ScreenCapturer(Rectangle bounds)
    {
        _bounds = bounds;
        _frame = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        _previousPixels = new byte[checked(bounds.Width * bounds.Height * 4)];
        _currentPixels = new byte[_previousPixels.Length];
    }

    public Bitmap Frame => _frame;

    public int Width => _bounds.Width;

    public int Height => _bounds.Height;

    public string Name => "GDI";

    /// <summary>화면을 캡처하고, 직전 캡처와 픽셀이 달라졌으면 true를 반환합니다.</summary>
    public bool Capture()
    {
        using (Graphics graphics = Graphics.FromImage(_frame))
        {
            // 화면이 잠겨 있거나 UAC 보안 데스크톱이면 Win32Exception이 발생합니다.
            graphics.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, _bounds.Size, CopyPixelOperation.SourceCopy);

            IntPtr deviceContext = graphics.GetHdc();
            try
            {
                NativeMethods.DrawCursor(deviceContext, _bounds.Left, _bounds.Top);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }

        return DetectChange();
    }

    private bool DetectChange()
    {
        BitmapData data = _frame.LockBits(
            new Rectangle(0, 0, _bounds.Width, _bounds.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            // 32bpp는 한 줄이 항상 4바이트 정렬이므로 Stride == Width * 4 입니다.
            Marshal.Copy(data.Scan0, _currentPixels, 0, _currentPixels.Length);
        }
        finally
        {
            _frame.UnlockBits(data);
        }

        bool changed = !_hasPrevious || !_currentPixels.AsSpan().SequenceEqual(_previousPixels);
        (_previousPixels, _currentPixels) = (_currentPixels, _previousPixels);
        _hasPrevious = true;
        return changed;
    }

    public void Dispose()
    {
        _frame.Dispose();
    }
}
