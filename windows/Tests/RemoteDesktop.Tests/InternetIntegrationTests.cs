using System.Drawing;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using RemoteDesktop.Client;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;
using Signaling.Server;

namespace RemoteDesktop.Tests;

/// <summary>
/// STEP 7: 실제 시그널링 서버(Kestrel) + Host 엔진 + WebRTC(SIPSorcery) + Windows Client 코드.
/// 같은 PC 안에서 ICE host 후보로 연결되므로 외부 STUN/TURN 없이 테스트합니다.
/// </summary>
public sealed class InternetIntegrationTests : IAsyncLifetime
{
    private WebApplication? _signaling;
    private Uri _ws = null!;
    private Uri _http = null!;

    public async Task InitializeAsync()
    {
        string data = Directory.CreateTempSubdirectory("rd-signal-").FullName;
        _signaling = SignalingApp.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Signaling:DataDirectory"] = data;
            builder.Configuration["Signaling:StunUrls:0"] = ""; // 테스트는 로컬 후보만 사용
        });
        await _signaling.StartAsync();
        _http = new Uri(_signaling.Urls.First());
        _ws = new Uri($"ws://{_http.Authority}/ws");
    }

    public async Task DisposeAsync()
    {
        if (_signaling is not null)
        {
            await _signaling.StopAsync();
            await _signaling.DisposeAsync();
        }
    }

    private async Task<bool> IsOnlineAsync(string hostId)
    {
        using var http = new HttpClient { BaseAddress = _http };
        var result = await http.GetFromJsonAsync<JsonElement>($"/api/status?ids={hostId}");
        return result.GetProperty("online").GetProperty(hostId).GetBoolean();
    }

    private async Task WaitOnlineAsync(string hostId)
    {
        for (int i = 0; i < 100; i++)
        {
            if (await IsOnlineAsync(hostId))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"{hostId} did not register");
    }

    private TestHost NewHost(HostOptions? options = null, string? hostId = null) =>
        new((options ?? new HostOptions()) with { SignalingServer = _ws, EnableLan = false }, hostId: hostId);

    [Fact]
    public async Task ConnectByHostIdOverWebRtcAndStream()
    {
        await using var host = NewHost();
        await WaitOnlineAsync(host.Settings.HostId);

        using var connection = await host.ConnectInternetAsync(_ws, new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
        Assert.Equal("webrtc", connection.TransportKind);

        var frame = new TaskCompletionSource<Size>();
        connection.FrameReceived += (bitmap, header) =>
        {
            frame.TrySetResult(bitmap.Size);
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        connection.StartReceiving();

        // 1080p JPEG 프레임(수백 KB)이 16 KB 조각으로 나뉘어 Data Channel을 지나 다시 합쳐져야 함
        Size size = await frame.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(connection.ScreenWidth, size.Width);
    }

    [Fact]
    public async Task WrongCodeOverInternetIsRejected()
    {
        await using var host = NewHost();
        await WaitOnlineAsync(host.Settings.HostId);

        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() =>
            host.ConnectInternetAsync(_ws, new LoginRequest(AuthMethods.AccessCode, "WRONGWRONG", false)));
        Assert.Equal(AuthErrorCodes.InvalidCredentials, error.ErrorCode);
    }

    [Fact]
    public async Task OfflineHost()
    {
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() =>
            RemoteHostConnection.ConnectInternetAsync(_ws, "HOST-NOBODY", CancellationToken.None));
        Assert.Contains("오프라인", error.Message);
    }

    [Fact]
    public async Task HostIdCannotBeHijackedWithAnotherKey()
    {
        await using var owner = NewHost(hostId: "HOST-OWNER1");
        await WaitOnlineAsync("HOST-OWNER1");

        // 같은 Host ID, 다른 서명 키 → 서버가 거부. 원래 Host는 계속 온라인이고 연결 가능
        await using var impostor = NewHost(hostId: "HOST-OWNER1");
        await Task.Delay(1500);
        Assert.True(await IsOnlineAsync("HOST-OWNER1"));

        using var connection = await owner.ConnectInternetAsync(_ws, new LoginRequest(AuthMethods.AccessCode, owner.Server.AccessCode, false));
        Assert.Equal("HOST-OWNER1", connection.HostId);
    }

    [Fact]
    public async Task HostGoesOfflineWhenStopped()
    {
        var host = NewHost();
        await WaitOnlineAsync(host.Settings.HostId);
        await host.DisposeAsync();

        for (int i = 0; i < 50 && await IsOnlineAsync(host.Settings.HostId); i++)
        {
            await Task.Delay(100);
        }

        Assert.False(await IsOnlineAsync(host.Settings.HostId));
    }
}

public class WebRtcBindingTests
{
    private const string Offer = "v=0\r\na=fingerprint:sha-256 AA:BB:CC\r\n";
    private const string Answer = "v=0\r\na=fingerprint:sha-256 11:22:33\r\n";

    [Fact]
    public void BindingChangesIfAnyFingerprintIsReplaced()
    {
        byte[] real = WebRtcBinding.Compute(Offer, Answer);
        Assert.Equal(real, WebRtcBinding.Compute(Offer.ToLowerInvariant().Replace("v=0", "v=0"), Answer));
        Assert.NotEqual(real, WebRtcBinding.Compute(Offer.Replace("AA", "AB"), Answer));
        Assert.NotEqual(real, WebRtcBinding.Compute(Offer, Answer.Replace("11", "12")));
        Assert.NotEqual(real, WebRtcBinding.Compute(Answer, Offer)); // 순서도 중요
    }

    [Fact]
    public void VectorMatchesDart()
    {
        // client_app/test/webrtc_binding_test.dart와 같은 값
        Assert.Equal("258C072E6C22600CC44BBCA029B82DE62BFF054609541C029E3A1E14762A54C6", Convert.ToHexString(WebRtcBinding.Compute(Offer, Answer)));
    }
}
