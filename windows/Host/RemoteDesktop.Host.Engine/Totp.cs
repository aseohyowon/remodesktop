using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// TOTP (RFC 6238): Google Authenticator, Microsoft Authenticator 등과 호환되는 6자리 코드, 30초 주기.
/// </summary>
public sealed class Totp
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly byte[] _secret;
    private long _lastUsedStep = -1;

    public Totp(byte[] secret)
    {
        _secret = secret;
    }

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>
    /// 앞뒤 1주기(±30초)까지 허용합니다. 한 번 사용한 코드(주기)는 다시 받지 않습니다(재사용 공격 방지).
    /// </summary>
    public bool Verify(string? code, DateTimeOffset now)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        long currentStep = now.ToUnixTimeSeconds() / StepSeconds;
        for (long step = currentStep - 1; step <= currentStep + 1; step++)
        {
            if (step > _lastUsedStep
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Generate(step)), Encoding.ASCII.GetBytes(code)))
            {
                _lastUsedStep = step;
                return true;
            }
        }

        return false;
    }

    public string GenerateAt(DateTimeOffset time) => Generate(time.ToUnixTimeSeconds() / StepSeconds);

    private string Generate(long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(_secret, counter);

        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public static string ToBase32(byte[] data)
    {
        var output = new StringBuilder();
        int buffer = 0;
        int bits = 0;
        foreach (byte value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    /// <summary>인증 앱에 등록할 URI (QR 코드로 만들거나 앱에 직접 입력)</summary>
    public static string ToOtpAuthUri(byte[] secret, string hostId) =>
        $"otpauth://totp/RemoteDesktop:{Uri.EscapeDataString(hostId)}?secret={ToBase32(secret)}&issuer=RemoteDesktop&digits={Digits}&period={StepSeconds}";
}
