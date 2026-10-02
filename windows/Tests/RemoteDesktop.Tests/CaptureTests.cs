using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using RemoteDesktop.Host.Engine;
using Xunit.Abstractions;

namespace RemoteDesktop.Tests;

/// <summary>STEP 8: Desktop Duplication 캡처가 GDI와 같은 화면을 더 빠르게 가져오는지</summary>
public class CaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void DesktopDuplicationMatchesGdi()
    {
        NativeMethods.EnablePerMonitorDpiAwareness();
        Rectangle bounds = Screen.PrimaryScreen!.Bounds;

        using IScreenCapturer dxgi = ScreenCapturerFactory.Create(bounds);
        using var gdi = new ScreenCapturer(bounds);
        output.WriteLine($"Capturer: {dxgi.Name}");

        Assert.True(dxgi.Capture(), "첫 캡처는 항상 새 화면이어야 합니다");
        gdi.Capture();
        Assert.Equal(bounds.Size, dxgi.Frame.Size);

        double psnr = Psnr(dxgi.Frame, gdi.Frame);
        output.WriteLine($"DXGI vs GDI PSNR {psnr:F1} dB");
        Assert.True(psnr > 20, $"두 방식의 화면이 너무 다릅니다 ({psnr:F1} dB)");

        var sw = Stopwatch.StartNew();
        int changed = 0;
        for (int i = 0; i < 30; i++)
        {
            if (dxgi.Capture()) changed++;
        }

        double dxgiMs = sw.Elapsed.TotalMilliseconds / 30;
        sw.Restart();
        for (int i = 0; i < 10; i++)
        {
            gdi.Capture();
        }

        double gdiMs = sw.Elapsed.TotalMilliseconds / 10;
        output.WriteLine($"{dxgi.Name}: {dxgiMs:F2} ms/call ({changed}/30 changed), GDI: {gdiMs:F2} ms/call");
    }

    private static double Psnr(Bitmap a, Bitmap b)
    {
        var rect = new Rectangle(0, 0, a.Width, a.Height);
        BitmapData da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        BitmapData db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            double sum = 0;
            long count = 0;
            unsafe
            {
                for (int y = 0; y < a.Height; y += 4)
                {
                    byte* ra = (byte*)da.Scan0 + y * da.Stride;
                    byte* rb = (byte*)db.Scan0 + y * db.Stride;
                    for (int x = 0; x < a.Width * 4; x += 16)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            double d = ra[x + c] - rb[x + c];
                            sum += d * d;
                            count++;
                        }
                    }
                }
            }

            double mse = sum / count;
            return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }
}
