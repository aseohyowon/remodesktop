using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Host;

/// <summary>
/// STEP 1: 전체 가상 화면을 BMP 파일 하나로 저장합니다.
/// 실행: RemoteDesktop.Host.exe capture [출력파일.bmp]
/// </summary>
internal static class StepOneCapture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const int BmpHeaderSize = 14;

    public static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("화면 캡처는 Windows에서만 실행할 수 있습니다.");
            return 1;
        }

        if (args.Length > 1)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            string outputPath = GetOutputPath(args);
            CaptureVirtualScreen(outputPath);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"화면 캡처 실패: {exception.Message}");
            return 1;
        }
    }

    private static string GetOutputPath(string[] args)
    {
        string path = args.Length == 1
            ? args[0]
            : Path.Combine(
                Environment.CurrentDirectory,
                "captures",
                $"desktop-{DateTime.Now:yyyyMMdd-HHmmss}.bmp");

        if (!Path.GetExtension(path).Equals(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("출력 파일 확장자는 .bmp여야 합니다.");
        }

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static void CaptureVirtualScreen(string outputPath)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("캡처할 데스크톱 화면을 찾을 수 없습니다.");
        }

        int pixelLength = checked(width * height * 4);
        byte[] pixels = new byte[pixelLength];
        IntPtr desktopDc = GetDC(IntPtr.Zero);

        if (desktopDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousBitmap = IntPtr.Zero;

        try
        {
            memoryDc = CreateCompatibleDC(desktopDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            bitmap = CreateCompatibleBitmap(desktopDc, width, height);
            if (bitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            previousBitmap = SelectObject(memoryDc, bitmap);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!BitBlt(memoryDc, 0, 0, width, height, desktopDc, left, top, SrcCopy | CaptureBlt))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            IntPtr deselectedBitmap = SelectObject(memoryDc, previousBitmap);
            if (deselectedBitmap == IntPtr.Zero || deselectedBitmap == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            previousBitmap = IntPtr.Zero;

            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
                SizeImage = (uint)pixelLength,
                XPixelsPerMeter = 2835,
                YPixelsPerMeter = 2835
            };

            int rowsCopied = GetDIBits(
                desktopDc,
                bitmap,
                0,
                (uint)height,
                pixels,
                ref header,
                DibRgbColors);

            if (rowsCopied != height)
            {
                throw new InvalidOperationException($"화면 픽셀을 읽지 못했습니다 ({rowsCopied}/{height} 줄).");
            }

            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = byte.MaxValue;
            }

            WriteBitmap(outputPath, width, height, pixels);
            Console.WriteLine($"화면 캡처 완료: {outputPath}");
            Console.WriteLine($"크기: {width} x {height} 픽셀 (전체 가상 화면)");
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousBitmap);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    private static void WriteBitmap(string outputPath, int width, int height, byte[] pixels)
    {
        uint imageSize = checked((uint)pixels.Length);
        uint fileSize = checked((uint)(BmpHeaderSize + Marshal.SizeOf<BitmapInfoHeader>()) + imageSize);

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0x4D42);
        writer.Write(fileSize);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((uint)(BmpHeaderSize + Marshal.SizeOf<BitmapInfoHeader>()));
        writer.Write((uint)Marshal.SizeOf<BitmapInfoHeader>());
        writer.Write(width);
        writer.Write(-height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(BiRgb);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("사용법: RemoteDesktop.Host.exe capture [출력파일.bmp]");
        Console.WriteLine("출력 경로를 생략하면 현재 폴더의 captures 폴더에 저장합니다.");
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr deviceContext,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfoHeader bitmapInfo,
        uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }
}
