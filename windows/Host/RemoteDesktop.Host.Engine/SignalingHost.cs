using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport.WebRtc;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 인터넷 연결 (STEP 7)
/// 1) 시그널링 서버에 WebSocket으로 접속 (Host → 서버 방향, Host의 포트를 열 필요 없음)
/// 2) Host ID 소유 증명: 서버가 준 nonce에 ECDSA 서명
/// 3) Client의 연결 요청이 오면 WebRTC offer 생성 → SDP/ICE 교환 → Data Channel이 열리면 LAN과 같은 세션 실행
/// 연결이 끊기면 1초 → 2초 → ... 최대 60초 간격으로 다시 접속합니다.
/// </summary>
internal sealed class SignalingHost(HostServer server)
{
    private const int MaxPendingPeers = 8;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Uri uri = server.Options.SignalingServer!;
        TimeSpan backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = await SignalingSocket.ConnectAsync(uri, cancellationToken);
                if (await RegisterAsync(socket, cancellationToken))
                {
                    backoff = TimeSpan.FromSeconds(1);
                    await ServeAsync(socket, cancellationToken);
                    Log.Warn("시그널링 서버 연결이 끊겼습니다.");
                }
                else
                {
                    backoff = MaxBackoff;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.Warn($"시그널링 서버({uri.Host}) 연결 실패: {exception.Message}");
            }

            Log.Info($"{backoff.TotalSeconds:F0}초 후 시그널링 서버에 다시 연결합니다.");
            try
            {
                await Task.Delay(backoff, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task<bool> RegisterAsync(SignalingSocket socket, CancellationToken cancellationToken)
    {
        string hostId = server.Settings.HostId;
        using ECDsa key = server.Settings.GetOrCreateSignalingKey();

        await socket.SendAsync(new HostHelloSignal(hostId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())), cancellationToken);

        while (await socket.ReceiveAsync(cancellationToken) is { } message)
        {
            switch (message)
            {
                case HostChallengeSignal challenge:
                    byte[] signature = key.SignData(SignalingJson.HostAuthPayload(hostId, challenge.Nonce), HashAlgorithmName.SHA256);
                    await socket.SendAsync(new HostAuthSignal(Convert.ToBase64String(signature)), cancellationToken);
                    break;

                case HostRegisteredSignal:
                    Log.Info($"시그널링 서버에 등록됨: {hostId} (인터넷 연결 대기)");
                    return true;

                case ErrorSignal error:
                    Log.Error($"시그널링 서버 등록 실패: {error.Message} ({error.Code})");
                    return false;
            }
        }

        return false;
    }

    private async Task ServeAsync(SignalingSocket socket, CancellationToken cancellationToken)
    {
        var peers = new ConcurrentDictionary<string, WebRtcPeer>();
        Task Send(SignalMessage message) => socket.SendAsync(message, CancellationToken.None);

        try
        {
            while (await socket.ReceiveAsync(cancellationToken) is { } message)
            {
                switch (message)
                {
                    case ConnectRequestSignal request:
                        IPAddress? address = IPAddress.TryParse(request.ClientAddress, out var parsed) ? parsed : null;
                        if (server.Throttle.IsLocked(address, out _) || peers.Count >= MaxPendingPeers)
                        {
                            Log.Warn($"Rejected internet connection from {address}: 차단 중이거나 연결 시도가 너무 많음");
                            await Send(new SessionEndSignal(request.SessionId, "rejected"));
                            break;
                        }

                        var peer = new WebRtcPeer(request.SessionId, isHost: true, request.IceServers, Send);
                        peers[request.SessionId] = peer;
                        await peer.StartAsHostAsync();
                        _ = RunPeerAsync(peer, address, request.IceServers, peers, cancellationToken);
                        break;

                    case SdpSignal sdp when peers.TryGetValue(sdp.SessionId, out var target):
                        await target.HandleAsync(sdp);
                        break;

                    case IceSignal ice when peers.TryGetValue(ice.SessionId, out var target):
                        await target.HandleAsync(ice);
                        break;

                    case SessionEndSignal end when peers.TryGetValue(end.SessionId, out var target):
                        await target.HandleAsync(end);
                        break;

                    case ErrorSignal error:
                        Log.Debug($"Signaling error: {error.Code} {error.Message}");
                        break;
                }
            }
        }
        finally
        {
            // 협상 중이던 연결은 정리 (이미 열린 세션은 P2P로 계속 동작)
            foreach (var pending in peers.Values)
            {
                pending.Dispose();
            }
        }
    }

    private async Task RunPeerAsync(WebRtcPeer peer, IPAddress? address, IceServerInfo[] iceServers, ConcurrentDictionary<string, WebRtcPeer> peers, CancellationToken cancellationToken)
    {
        WebRtcConnection connection;
        try
        {
            connection = await peer.WaitOpenAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Log.Warn($"WebRTC 연결 실패 ({address}): {exception.Message}");
            peers.TryRemove(peer.SessionId, out _);
            peer.Dispose();
            return;
        }

        peers.TryRemove(peer.SessionId, out _);
        string description = $"{address?.ToString() ?? "unknown"} (internet)";
        Log.Info($"Client connected: {description}");
        var transport = new HostTransport(connection.Stream, connection.ChannelBinding, address, description, "webrtc", iceServers);
        await server.RunSessionAsync(transport, null, cancellationToken);
        Log.Info($"Client disconnected: {description}");
    }
}
