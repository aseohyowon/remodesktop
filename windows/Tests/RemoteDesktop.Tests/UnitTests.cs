using System.Net;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Tests;

public class TotpTests
{
    // RFC 6238 부록 B 테스트 벡터 (SHA-1, 8자리 중 마지막 6자리)
    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void MatchesRfcVectors(long unixSeconds, string expected)
    {
        var totp = new Totp("12345678901234567890"u8.ToArray());
        Assert.Equal(expected, totp.GenerateAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public void RejectsReuseAndWrongCodes()
    {
        byte[] secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var totp = new Totp(secret);
        string code = totp.GenerateAt(now);

        Assert.False(totp.Verify("12345", now));
        Assert.False(totp.Verify("abcdef", now));
        Assert.True(totp.Verify(code, now));
        Assert.False(totp.Verify(code, now)); // 같은 코드 재사용 금지
    }

    [Fact]
    public void Base32()
    {
        Assert.Equal("MZXW6YTBOI", Totp.ToBase32("foobar"u8.ToArray()));
    }
}

public class ThrottleTests
{
    [Fact]
    public void LocksAddressAfterFiveFailures()
    {
        var now = DateTime.UtcNow;
        var throttle = new AuthThrottle(() => now);
        var ip = IPAddress.Parse("192.168.0.9");

        for (int i = 0; i < 4; i++)
        {
            Assert.False(throttle.RecordFailure(ip));
        }

        Assert.True(throttle.RecordFailure(ip));
        Assert.True(throttle.IsLocked(ip, out _));
        Assert.False(throttle.IsLocked(IPAddress.Parse("192.168.0.10"), out _));

        now += TimeSpan.FromMinutes(6);
        Assert.False(throttle.IsLocked(ip, out _));
    }

    [Fact]
    public void GlobalLockStopsDistributedAttack()
    {
        var now = DateTime.UtcNow;
        var throttle = new AuthThrottle(() => now);
        for (int i = 0; i < 30; i++)
        {
            throttle.RecordFailure(IPAddress.Parse($"10.0.{i}.1")); // 30개의 서로 다른 IP
        }

        Assert.True(throttle.IsLocked(IPAddress.Parse("10.9.9.9"), out _));
        now += TimeSpan.FromMinutes(3);
        Assert.False(throttle.IsLocked(IPAddress.Parse("10.9.9.9"), out _));
    }

    [Fact]
    public void SuccessResetsAddress()
    {
        var throttle = new AuthThrottle();
        var ip = IPAddress.Parse("192.168.0.9");
        for (int i = 0; i < 4; i++) throttle.RecordFailure(ip);
        throttle.RecordSuccess(ip);
        Assert.False(throttle.RecordFailure(ip));
    }
}

public class NetworkAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("100.100.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.0.5", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void ClassifiesAddresses(string address, bool expected)
    {
        Assert.Equal(expected, NetworkAddress.IsPrivate(IPAddress.Parse(address)));
    }
}

public class AuthProofTests
{
    private static byte[] Range(int start) => Enumerable.Range(start, 32).Select(i => (byte)i).ToArray();

    // Dart(client_app/test/protocol_test.dart)와 같은 벡터
    [Fact]
    public void AccessCodeVectorMatchesDart()
    {
        byte[] key = AuthProof.DeriveAccessCodeKey("k7mpq-2xrta");
        Assert.Equal("+sGhO+oce/K8SdCSIcU+oTKQhL6fMoYk5Hu5FQKV6uA=",
            Convert.ToBase64String(AuthProof.Compute(AuthRole.Client, key, Range(0), Range(32), Range(64))));
    }

    [Fact]
    public void PasswordKeyIsPbkdf2Sha256()
    {
        // RFC 7914 §11 PBKDF2-HMAC-SHA256 벡터: P="passwd", S="salt", c=1, dkLen=64 → 앞 32바이트
        byte[] key = AuthProof.DerivePasswordKey("passwd", "salt"u8, 1);
        Assert.Equal("55AC046E56E3089FEC1691C22544B605F94185216DDE0465E68B9D57C20DACBC", Convert.ToHexString(key));
    }
}
