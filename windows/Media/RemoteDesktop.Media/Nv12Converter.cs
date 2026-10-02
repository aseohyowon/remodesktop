namespace RemoteDesktop.Media;

/// <summary>
/// BGRA(화면) ↔ NV12(H.264 인코더/디코더 입력·출력) 변환. BT.601 limited range.
/// NV12 = Y 평면(가로×세로) + UV 교차 평면(가로×세로/2). 가로·세로는 짝수여야 합니다.
/// 행 단위로 병렬 처리해 1080p에서 수 ms 안에 끝납니다.
/// </summary>
public static unsafe class Nv12Converter
{
    public static int Nv12Size(int width, int height) => width * height * 3 / 2;

    public static void BgraToNv12(byte* bgra, int bgraStride, int width, int height, byte[] nv12)
    {
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("가로·세로는 짝수여야 합니다.");
        }

        fixed (byte* dst = nv12)
        {
            byte* y = dst;
            byte* uv = dst + width * height;
            nint srcBase = (nint)bgra;
            nint yBase = (nint)y;
            nint uvBase = (nint)uv;

            // 2줄씩 처리: Y는 각 픽셀, UV는 2×2 블록 평균
            Parallel.For(0, height / 2, pair =>
            {
                byte* row0 = (byte*)srcBase + pair * 2 * bgraStride;
                byte* row1 = row0 + bgraStride;
                byte* y0 = (byte*)yBase + pair * 2 * width;
                byte* y1 = y0 + width;
                byte* uvRow = (byte*)uvBase + pair * width;

                for (int x = 0; x < width; x += 2)
                {
                    int b00 = row0[x * 4], g00 = row0[x * 4 + 1], r00 = row0[x * 4 + 2];
                    int b01 = row0[x * 4 + 4], g01 = row0[x * 4 + 5], r01 = row0[x * 4 + 6];
                    int b10 = row1[x * 4], g10 = row1[x * 4 + 1], r10 = row1[x * 4 + 2];
                    int b11 = row1[x * 4 + 4], g11 = row1[x * 4 + 5], r11 = row1[x * 4 + 6];

                    y0[x] = Y(r00, g00, b00);
                    y0[x + 1] = Y(r01, g01, b01);
                    y1[x] = Y(r10, g10, b10);
                    y1[x + 1] = Y(r11, g11, b11);

                    int r = (r00 + r01 + r10 + r11 + 2) >> 2;
                    int g = (g00 + g01 + g10 + g11 + 2) >> 2;
                    int b = (b00 + b01 + b10 + b11 + 2) >> 2;
                    uvRow[x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    uvRow[x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            });
        }
    }

    /// <summary>
    /// NV12 → BGRA. stride는 NV12 한 줄의 바이트 수(디코더 출력은 16의 배수로 정렬될 수 있음),
    /// planeHeight는 Y 평면의 줄 수(예: 1080p 디코더 출력은 1088일 수 있음).
    /// </summary>
    public static void Nv12ToBgra(byte* nv12, int stride, int planeHeight, int width, int height, byte* bgra, int bgraStride)
    {
        nint yBase = (nint)nv12;
        nint uvBase = (nint)(nv12 + stride * planeHeight);
        nint dstBase = (nint)bgra;

        Parallel.For(0, height, row =>
        {
            byte* y = (byte*)yBase + row * stride;
            byte* uv = (byte*)uvBase + (row >> 1) * stride;
            byte* dst = (byte*)dstBase + row * bgraStride;

            for (int x = 0; x < width; x++)
            {
                int c = 298 * (y[x] - 16);
                int d = uv[x & ~1] - 128;
                int e = uv[(x & ~1) + 1] - 128;

                dst[x * 4] = Clamp((c + 516 * d + 128) >> 8);
                dst[x * 4 + 1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                dst[x * 4 + 2] = Clamp((c + 409 * e + 128) >> 8);
                dst[x * 4 + 3] = 255;
            }
        });
    }

    private static byte Y(int r, int g, int b) => (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
