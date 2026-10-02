using System.Drawing;
using RemoteDesktop.Client;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Tests;

/// <summary>STEP 6 인증: 실제 Host 엔진 + 실제 TLS + 실제 Windows Client 코드</summary>
public class AuthIntegrationTests
{
    private static LoginRequest Code(string code, bool remember = false) => new(AuthMethods.AccessCode, code, remember);

    private static LoginRequest Password(string password, bool remember = false) => new(AuthMethods.Password, password, remember);

    [Fact]
    public async Task AccessCodeLoginStreamsFrames()
    {
        await using var host = new TestHost();
        using var connection = await host.ConnectAsync(Code(host.Server.AccessCode.ToLowerInvariant()));

        Assert.Equal(host.Settings.HostId, connection.HostId);
        Assert.True(connection.ScreenWidth > 0);

        var frames = new TaskCompletionSource<Size>();
        connection.FrameReceived += (bitmap, header) =>
        {
            frames.TrySetResult(bitmap.Size);
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        connection.StartReceiving();

        Size size = await frames.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(connection.ScreenWidth, size.Width);
    }

    [Fact]
    public async Task WrongAccessCodeIsRejected()
    {
        await using var host = new TestHost();
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code("WRONG-CODE1")));
        Assert.Equal(AuthErrorCodes.InvalidCredentials, error.ErrorCode);
    }

    [Fact]
    public async Task PasswordLogin()
    {
        await using var host = new TestHost(configure: s => s.SetPassword("correct horse battery"));

        await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Password("wrong password")));
        using var connection = await host.ConnectAsync(Password("correct horse battery"));
        Assert.Equal(host.Settings.HostId, connection.HostId);
    }

    [Fact]
    public async Task PasswordIsNotStoredInSettingsFile()
    {
        await using var host = new TestHost(configure: s => s.SetPassword("correct horse battery"));
        string json = File.ReadAllText(Path.Combine(host.Directory, "host.json"));
        Assert.DoesNotContain("correct horse battery", json);
        Assert.Contains("key_protected", json);
    }

    [Fact]
    public async Task AccessCodeCanBeDisabled()
    {
        await using var host = new TestHost(new HostOptions { AccessCodeEnabled = false }, configure: s => s.SetPassword("correct horse battery"));
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code(host.Server.AccessCode)));
        Assert.Contains("비밀번호", error.Message);
    }

    [Fact]
    public async Task TrustedDeviceRegistrationAndRevocation()
    {
        await using var host = new TestHost();
        DeviceCredentialStore devices = host.NewDeviceStore();

        // 1) 접속 코드 + "이 PC 기억하기"
        using (await host.ConnectAsync(Code(host.Server.AccessCode, remember: true), devices: devices))
        {
        }

        Assert.True(devices.Has(host.Settings.HostId));
        Assert.Single(host.Settings.ListDevices());

        // 2) 다음 연결은 비밀값 없이 장치 인증으로 성공
        await Task.Delay(300);
        using (var again = await host.ConnectAsync(Code(""), devices: devices))
        {
            Assert.Equal(host.Settings.HostId, again.HostId);
        }

        // 3) Host에서 등록 해제 → 실패하고 Client에 저장된 자격 증명도 삭제
        host.Settings.RevokeDevice("all");
        await Task.Delay(300);
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code(""), devices: devices));
        Assert.Equal(AuthErrorCodes.DeviceRevoked, error.ErrorCode);
        Assert.False(devices.Has(host.Settings.HostId));
    }

    /// <summary>
    /// 회귀 테스트: 실행 중인 Host 옆에서(다른 프로세스/인스턴스) 바꾼 설정이 재시작 없이 바로 적용되고,
    /// Host가 나중에 저장해도 그 변경을 덮어쓰지 않아야 합니다.
    /// </summary>
    [Fact]
    public async Task SettingsChangedElsewhereApplyImmediately()
    {
        await using var host = new TestHost();
        DeviceCredentialStore devices = host.NewDeviceStore();
        string path = Path.Combine(host.Directory, "host.json");

        // 다른 인스턴스(= CLI의 password set)로 비밀번호 설정 → 실행 중인 Host가 바로 받아야 함
        HostSettings.Load(path).SetPassword("set from cli 123");
        using (await host.ConnectAsync(Password("set from cli 123", remember: true), devices: devices))
        {
        }

        // Host가 장치를 등록하며 저장했어도 비밀번호는 그대로여야 함
        Assert.True(HostSettings.Load(path).HasPassword);

        // 다른 인스턴스(= CLI의 devices revoke all)로 해제 → 다음 연결에서 바로 거부
        await Task.Delay(300);
        HostSettings.Load(path).RevokeDevice("all");
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code(""), devices: devices));
        Assert.Equal(AuthErrorCodes.DeviceRevoked, error.ErrorCode);
    }

    [Fact]
    public async Task TwoFactorRequiredForCodeLogin()
    {
        byte[] secret = Totp.GenerateSecret();
        await using var host = new TestHost(configure: s => s.SetTotpSecret(secret));

        // 코드 없이 → 실패
        var noCode = await Assert.ThrowsAsync<ConnectionFailedException>(() =>
            host.ConnectAsync(Code(host.Server.AccessCode), new TestPrompts(() => "")));
        Assert.Equal(AuthErrorCodes.TotpRequired, noCode.ErrorCode);

        // 올바른 코드 → 성공
        string current = new Totp(secret).GenerateAt(DateTimeOffset.UtcNow);
        var prompts = new TestPrompts(() => current);
        using (await host.ConnectAsync(Code(host.Server.AccessCode), prompts))
        {
        }

        Assert.Equal(1, prompts.TotpAsked);

        // 같은 코드 재사용 → 실패
        await Task.Delay(300);
        await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code(host.Server.AccessCode), new TestPrompts(() => current)));
    }

    [Fact]
    public async Task TrustedDeviceSkipsTwoFactorAndApproval()
    {
        byte[] secret = Totp.GenerateSecret();
        var callbacks = new TestCallbacks(approve: true);
        await using var host = new TestHost(new HostOptions { RequireApproval = true }, callbacks, s => s.SetTotpSecret(secret));
        DeviceCredentialStore devices = host.NewDeviceStore();

        string code = new Totp(secret).GenerateAt(DateTimeOffset.UtcNow);
        using (await host.ConnectAsync(Code(host.Server.AccessCode, remember: true), new TestPrompts(() => code), devices))
        {
        }

        Assert.Equal(1, callbacks.ApprovalRequests);

        await Task.Delay(300);
        var prompts = new TestPrompts(() => null);
        using (await host.ConnectAsync(Code(""), prompts, devices))
        {
        }

        Assert.Equal(0, prompts.TotpAsked);
        Assert.Equal(1, callbacks.ApprovalRequests);
    }

    [Fact]
    public async Task ApprovalDenied()
    {
        var callbacks = new TestCallbacks(approve: false);
        await using var host = new TestHost(new HostOptions { RequireApproval = true }, callbacks);
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code(host.Server.AccessCode)));
        Assert.Equal(AuthErrorCodes.Denied, error.ErrorCode);
        Assert.Equal(1, callbacks.ApprovalRequests);
    }

    [Fact]
    public async Task BruteForceLocksOutAfterFiveFailures()
    {
        await using var host = new TestHost();
        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code($"WRONG{i}")));
        }

        // 이제 올바른 코드도 거부 (TLS 전에 연결을 끊음)
        await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code(host.Server.AccessCode)));
    }

    [Fact]
    public async Task SecondClientIsRejectedWhileBusy()
    {
        await using var host = new TestHost();
        using var first = await host.ConnectAsync(Code(host.Server.AccessCode));
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code(host.Server.AccessCode)));
        Assert.Equal(AuthErrorCodes.Busy, error.ErrorCode);
    }

    [Fact]
    public async Task SessionExpires()
    {
        var callbacks = new TestCallbacks(approve: true);
        await using var host = new TestHost(new HostOptions { MaxSessionDuration = TimeSpan.FromSeconds(2) }, callbacks);
        using var connection = await host.ConnectAsync(Code(host.Server.AccessCode));

        var disconnected = new TaskCompletionSource<string>();
        connection.Disconnected += reason => disconnected.TrySetResult(reason);
        connection.FrameReceived += (bitmap, header) =>
        {
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        connection.StartReceiving();

        string message = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("만료", message);
    }

    [Fact]
    public async Task AccessLogHasNoSecrets()
    {
        await using var host = new TestHost(configure: s => s.SetPassword("correct horse battery"));
        using (await host.ConnectAsync(Password("correct horse battery")))
        {
        }

        await Assert.ThrowsAsync<ConnectionFailedException>(() => host.ConnectAsync(Code("SECRETWRONG")));
        string log = string.Join("\n", AccessLog.ReadLast(50));
        Assert.DoesNotContain("correct horse battery", log);
        Assert.DoesNotContain("SECRETWRONG", log);
        Assert.DoesNotContain(host.Server.AccessCode, log);
    }
}
