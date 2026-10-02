using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using RemoteDesktop.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesktop.Host.Engine;

/// <summary>화면 캡처 방식 공통 인터페이스 (GDI, Desktop Duplication)</summary>
public interface IScreenCapturer : IDisposable
{
    /// <summary>캡처 결과 (32bpp BGRA, 마우스 커서 포함)</summary>
    Bitmap Frame { get; }

    int Width { get; }

    int Height { get; }

    string Name { get; }

    /// <summary>새 화면을 가져옵니다. 이전과 달라졌으면(커서 이동 포함) true</summary>
    bool Capture();
}

public static class ScreenCapturerFactory
{
    /// <summary>Desktop Duplication을 먼저 시도하고, 안 되면 GDI로 캡처합니다.</summary>
    public static IScreenCapturer Create(Rectangle bounds)
    {
        try
        {
            return new DxgiCapturer(bounds);
        }
        catch (Exception exception) when (exception is SharpGenException or NotSupportedException or Win32Exception)
        {
            Log.Info($"Desktop Duplication을 사용할 수 없어 GDI 캡처를 사용합니다: {exception.Message}");
            return new ScreenCapturer(bounds);
        }
    }
}

/// <summary>
/// DXGI Desktop Duplication 캡처 (Windows 8 이상)
/// - GPU가 화면을 합성한 결과를 직접 받아 GDI보다 빠르고 CPU를 적게 씁니다.
/// - 화면이 바뀌지 않으면 프레임이 오지 않으므로 변경 감지가 공짜입니다.
/// - 잠금 화면/UAC 등으로 바뀌면 ACCESS_LOST가 나며, 다시 연결합니다.
/// </summary>
public sealed class DxgiCapturer : IScreenCapturer
{
    private readonly Rectangle _bounds;
    private readonly Bitmap _clean;   // 커서 없는 최신 화면
    private readonly Bitmap _frame;   // 커서를 그린 결과
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDXGIOutputDuplication? _duplication;
    private bool _hasImage;

    public DxgiCapturer(Rectangle bounds)
    {
        _bounds = bounds;
        _clean = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        _frame = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        Connect();
        Log.Info($"Capture: Desktop Duplication ({bounds.Width}x{bounds.Height})");
    }

    public Bitmap Frame => _frame;

    public int Width => _bounds.Width;

    public int Height => _bounds.Height;

    public string Name => "Desktop Duplication";

    public bool Capture()
    {
        try
        {
            return CaptureCore();
        }
        catch (SharpGenException exception)
        {
            // 보안 데스크톱(잠금/UAC), 드라이버 재설정 등: 연결을 버리고 호출 쪽이 잠시 후 다시 시도하게 합니다.
            Disconnect();
            throw new Win32Exception($"Desktop Duplication 오류: {exception.Message}");
        }
    }

    private bool CaptureCore()
    {
        if (_duplication is null)
        {
            Connect(); // ACCESS_LOST 이후 재연결
        }

        Result result = _duplication!.AcquireNextFrame(0, out OutduplFrameInfo info, out IDXGIResource? resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return !_hasImage ? Capture16ms() : false;
        }

        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            Disconnect();
            throw new Win32Exception("화면 접근 권한이 바뀌었습니다 (잠금 화면, UAC, 해상도 변경 등).");
        }

        result.CheckError();
        bool contentChanged = info.LastPresentTime != 0 || info.AccumulatedFrames > 0 || !_hasImage;
        bool cursorChanged = info.LastMouseUpdateTime != 0;

        try
        {
            if (contentChanged)
            {
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                _context!.CopyResource(_staging!, texture);
                CopyStagingTo(_clean);
                _hasImage = true;
            }
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }

        if (!contentChanged && !cursorChanged)
        {
            return false;
        }

        ComposeWithCursor();
        return true;
    }

    /// <summary>
    /// 처음에는 화면 변화가 없어도 한 장은 필요합니다. Desktop Duplication은 화면이 바뀌어야 프레임을 주므로
    /// 잠깐 기다려도 오지 않으면 GDI로 첫 화면을 한 번 가져옵니다.
    /// </summary>
    private bool Capture16ms()
    {
        Result result = _duplication!.AcquireNextFrame(200, out _, out IDXGIResource? resource);
        if (result.Failure)
        {
            using (Graphics graphics = Graphics.FromImage(_clean))
            {
                graphics.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, _bounds.Size, CopyPixelOperation.SourceCopy);
            }

            _hasImage = true;
            ComposeWithCursor();
            return true;
        }

        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            _context!.CopyResource(_staging!, texture);
            CopyStagingTo(_clean);
            _hasImage = true;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }

        ComposeWithCursor();
        return true;
    }

    private void CopyStagingTo(Bitmap target)
    {
        MappedSubresource mapped = _context!.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            BitmapData data = target.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                unsafe
                {
                    int rowBytes = Width * 4;
                    for (int y = 0; y < Height; y++)
                    {
                        Buffer.MemoryCopy(
                            (byte*)mapped.DataPointer + (long)y * mapped.RowPitch,
                            (byte*)data.Scan0 + (long)y * data.Stride,
                            rowBytes,
                            rowBytes);
                    }
                }
            }
            finally
            {
                target.UnlockBits(data);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private void ComposeWithCursor()
    {
        using Graphics graphics = Graphics.FromImage(_frame);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(_clean, 0, 0);
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

    private void Connect()
    {
        Disconnect();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // 캡처할 모니터가 연결된 GPU(어댑터)를 찾아 그 GPU로 장치를 만들어야 합니다(노트북 하이브리드 GPU 대응).
        for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success; adapterIndex++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success; outputIndex++)
                {
                    using (output)
                    {
                        var rect = output.Description.DesktopCoordinates;
                        if (rect.Left != _bounds.Left || rect.Top != _bounds.Top
                            || rect.Right - rect.Left != _bounds.Width || rect.Bottom - rect.Top != _bounds.Height)
                        {
                            continue;
                        }

                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null!, out _device, out _context).CheckError();
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        _duplication = output1.DuplicateOutput(_device);
                        _staging = _device!.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)_bounds.Width,
                            Height = (uint)_bounds.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.Read
                        });
                        return;
                    }
                }
            }
        }

        throw new NotSupportedException($"모니터({_bounds})에 해당하는 DXGI 출력을 찾지 못했습니다.");
    }

    private void Disconnect()
    {
        _staging?.Dispose();
        _duplication?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _staging = null;
        _duplication = null;
        _context = null;
        _device = null;
    }

    public void Dispose()
    {
        Disconnect();
        _clean.Dispose();
        _frame.Dispose();
    }
}
