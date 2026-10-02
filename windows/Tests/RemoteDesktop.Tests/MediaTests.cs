using System.Diagnostics;
using RemoteDesktop.Media;
using Xunit.Abstractions;

namespace RemoteDesktop.Tests;

/// <summary>STEP 8: 색 변환과 H.264 인코딩/디코딩 왕복 품질</summary>
public unsafe class MediaTests(ITestOutputHelper output)
{
    /// <summary>가로 그라데이션 + 움직이는 사각형 + 텍스트 같은 고대비 줄무늬</summary>
    private static byte[] MakeFrame(int width, int height, int t)
    {
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool box = x >= 40 + t * 8 && x < 200 + t * 8 && y >= 60 && y < 220;
                bool stripe = y > height - 80 && (x / 3) % 2 == 0;
                bgra[i] = (byte)(box ? 30 : stripe ? 0 : x * 255 / width);
                bgra[i + 1] = (byte)(box ? 200 : stripe ? 0 : y * 255 / height);
                bgra[i + 2] = (byte)(box ? 40 : stripe ? 0 : 128);
                bgra[i + 3] = 255;
            }
        }

        return bgra;
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                double d = a[i + c] - b[i + c];
                sum += d * d;
                count++;
            }
        }

        double mse = sum / count;
        return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }

    private static byte[] ToNv12(byte[] bgra, int width, int height)
    {
        var nv12 = new byte[Nv12Converter.Nv12Size(width, height)];
        fixed (byte* src = bgra)
        {
            Nv12Converter.BgraToNv12(src, width * 4, width, height, nv12);
        }

        return nv12;
    }

    [Fact]
    public void ColorConversionRoundTrip()
    {
        const int w = 320, h = 240;
        byte[] source = MakeFrame(w, h, 0);
        byte[] nv12 = ToNv12(source, w, h);
        var back = new byte[w * h * 4];
        fixed (byte* src = nv12)
        fixed (byte* dst = back)
        {
            Nv12Converter.Nv12ToBgra(src, w, h, w, h, dst, w * 4);
        }

        double psnr = Psnr(source, back);
        output.WriteLine($"NV12 round trip PSNR {psnr:F1} dB");
        Assert.True(psnr > 30, $"PSNR {psnr}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void H264RoundTrip(bool preferHardware)
    {
        const int w = 1280, h = 720;
        using var encoder = H264Encoder.Create(w, h, 30, 4_000_000, preferHardware);
        using var decoder = new H264Decoder();
        output.WriteLine($"Encoder: {encoder.Name} ({(encoder.IsHardware ? "GPU" : "CPU")})");

        byte[]? lastDecoded = null;
        byte[] lastSource = Array.Empty<byte>();
        var encodeTime = new Stopwatch();
        var decodeTime = new Stopwatch();
        int outputs = 0;
        bool sawSps = false;

        for (int t = 0; t < 20; t++)
        {
            lastSource = MakeFrame(w, h, t);
            encodeTime.Start();
            byte[] encoded = encoder.Encode(ToNv12(lastSource, w, h), forceKeyFrame: t == 0);
            encodeTime.Stop();
            if (encoded.Length == 0)
            {
                continue;
            }

            sawSps |= ContainsNal(encoded, 7);
            decodeTime.Start();
            byte[]? decoded = decoder.Decode(encoded, w, h);
            decodeTime.Stop();
            if (decoded is not null)
            {
                lastDecoded = decoded;
                outputs++;
            }
        }

        output.WriteLine($"outputs {outputs}/20, encode {encodeTime.ElapsedMilliseconds / 20.0:F1} ms/frame, decode {decodeTime.ElapsedMilliseconds / 20.0:F1} ms/frame");
        Assert.True(sawSps, "첫 키프레임에 SPS가 있어야 합니다");
        Assert.True(outputs >= 15, $"저지연 모드에서 대부분의 프레임이 바로 나와야 합니다 ({outputs})");
        Assert.NotNull(lastDecoded);
        Assert.Equal(w * h * 4, lastDecoded!.Length);

        // 마지막 디코딩 결과는 최근 입력 중 하나와 비슷해야 함 (인코더 지연 1~2프레임 허용)
        double best = Enumerable.Range(17, 3).Max(t => Psnr(MakeFrame(w, h, t), lastDecoded));
        output.WriteLine($"PSNR {best:F1} dB");
        Assert.True(best > 28, $"PSNR {best}");
    }

    [Fact]
    public void ForcedKeyFrameProducesIdr()
    {
        const int w = 640, h = 360;
        using var encoder = H264Encoder.Create(w, h, 30, 2_000_000, preferHardware: false);
        for (int t = 0; t < 5; t++)
        {
            encoder.Encode(ToNv12(MakeFrame(w, h, t), w, h), forceKeyFrame: t == 0);
        }

        byte[] forced = encoder.Encode(ToNv12(MakeFrame(w, h, 6), w, h), forceKeyFrame: true);
        byte[] following = encoder.Encode(ToNv12(MakeFrame(w, h, 7), w, h), forceKeyFrame: false);
        Assert.True(ContainsNal(forced, 5), "강제 키프레임은 IDR(NAL 5)이어야 합니다");
        Assert.False(ContainsNal(following, 5), "다음 프레임은 P-프레임이어야 합니다");
        Assert.True(encoder.SetBitrate(1_000_000));
    }

    /// <summary>Annex-B 안에 해당 NAL 유형(5=IDR, 7=SPS)이 있는지</summary>
    private static bool ContainsNal(byte[] data, int type)
    {
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1F) == type)
            {
                return true;
            }
        }

        return false;
    }
}
