using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using RemoteDesktop.Protocol;

namespace Signaling.Server;

/// <summary>
/// WebSocket 메시지 처리.
/// Host: host_hello → host_challenge → host_auth(서명) → host_registered → connect_request 수신 → sdp(offer)/ice 전송
/// Client: connect → connecting → sdp(offer)/ice 수신 → sdp(answer)/ice 전송. status로 온라인 조회
/// </summary>
public sealed class SignalingHub(
    HostRegistry registry,
    SessionBroker sessions,
    RateLimiter limiter,
    IceServerProvider iceServers,
    ILogger<SignalingHub> logger)
{
    public async Task RunAsync(WebSocket socket, IPAddress? address, CancellationToken cancellationToken)
    {
        var connection = new SignalConnection(socket, address);
        string? pendingHostId = null;
        string? pendingPublicKey = null;
        string? pendingNonce = null;

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                string? text = await ReceiveTextAsync(socket, cancellationToken);
                if (text is null)
                {
                    break;
                }

                if (!limiter.AllowMessage(connection.Id))
                {
                    await connection.SendAsync(new ErrorSignal("rate_limited", "메시지가 너무 많습니다."), cancellationToken);
                    continue;
                }

                SignalMessage? message = SignalingJson.Deserialize(text);
                switch (message)
                {
                    // ---------- Host 등록 ----------
                    case HostHelloSignal hello when connection.HostId is null && IsValidHostId(hello.HostId):
                        pendingHostId = hello.HostId;
                        pendingPublicKey = hello.PublicKey;
                        pendingNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                        await connection.SendAsync(new HostChallengeSignal(pendingNonce), cancellationToken);
                        break;

                    case HostAuthSignal auth when pendingHostId is not null && pendingNonce is not null && pendingPublicKey is not null:
                        if (!VerifyHostSignature(pendingHostId, pendingPublicKey, pendingNonce, auth.Signature))
                        {
                            await connection.SendAsync(new ErrorSignal("unauthorized", "서명이 올바르지 않습니다."), cancellationToken);
                            break;
                        }

                        if (!registry.TryClaim(pendingHostId, pendingPublicKey))
                        {
                            logger.LogWarning("Host ID {HostId} is registered with a different key ({Address})", pendingHostId, address);
                            await connection.SendAsync(new ErrorSignal("host_id_taken", "이 Host ID는 다른 PC가 사용 중입니다."), cancellationToken);
                            break;
                        }

                        connection.HostId = pendingHostId;
                        registry.SetOnline(pendingHostId, connection);
                        pendingNonce = null;
                        logger.LogInformation("Host online: {HostId} ({Address})", pendingHostId, address);
                        await connection.SendAsync(new HostRegisteredSignal(pendingHostId), cancellationToken);
                        break;

                    // ---------- Client 연결 요청 ----------
                    case ConnectSignal connect:
                        await HandleConnectAsync(connection, connect, cancellationToken);
                        break;

                    case StatusSignal status:
                        await connection.SendAsync(new StatusResultSignal(
                            status.HostIds.Take(50).Distinct().ToDictionary(id => id, registry.IsOnline)), cancellationToken);
                        break;

                    // ---------- 협상 중계 ----------
                    case SdpSignal sdp:
                        await RelayAsync(connection, sdp.SessionId, sdp, fromHostOnly: sdp.SdpType == "offer", cancellationToken);
                        break;

                    case IceSignal ice:
                        await RelayAsync(connection, ice.SessionId, ice, fromHostOnly: null, cancellationToken);
                        break;

                    case SessionEndSignal end:
                        await RelayAsync(connection, end.SessionId, end, fromHostOnly: null, cancellationToken);
                        sessions.Remove(end.SessionId);
                        break;

                    default:
                        await connection.SendAsync(new ErrorSignal("bad_request", "알 수 없거나 허용되지 않는 메시지입니다."), cancellationToken);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            registry.SetOffline(connection);
            limiter.Forget(connection.Id);
            foreach (var session in sessions.For(connection))
            {
                SignalConnection other = session.Host == connection ? session.Client : session.Host;
                await other.SendAsync(new SessionEndSignal(session.Id, "상대방 연결 종료"));
                sessions.Remove(session.Id);
            }

            if (connection.HostId is not null)
            {
                logger.LogInformation("Host offline: {HostId}", connection.HostId);
            }
        }
    }

    private async Task HandleConnectAsync(SignalConnection client, ConnectSignal connect, CancellationToken cancellationToken)
    {
        if (!limiter.AllowConnect(client.Address))
        {
            await client.SendAsync(new ErrorSignal("rate_limited", "연결 요청이 너무 많습니다. 잠시 후 다시 시도하세요."), cancellationToken);
            return;
        }

        SignalConnection? host = registry.Get(connect.HostId);
        if (host is null)
        {
            await client.SendAsync(new ErrorSignal("offline", $"{connect.HostId}가 오프라인입니다."), cancellationToken);
            return;
        }

        var session = sessions.Create(host, client);
        IceServerInfo[] servers = iceServers.Create(session.Id);
        logger.LogInformation("Session {SessionId}: client {Address} → {HostId}", session.Id, client.Address, connect.HostId);

        // Client에게 먼저 알려야 Host의 offer가 Client보다 먼저 도착하는 일이 없습니다.
        await client.SendAsync(new ConnectingSignal(session.Id, servers), cancellationToken);
        await host.SendAsync(new ConnectRequestSignal(session.Id, client.Address?.ToString(), servers), cancellationToken);
    }

    /// <summary>세션 참가자만 상대에게 전달할 수 있습니다. offer는 Host만, answer는 Client만 보낼 수 있습니다.</summary>
    private async Task RelayAsync(SignalConnection sender, string sessionId, SignalMessage message, bool? fromHostOnly, CancellationToken cancellationToken)
    {
        var session = sessions.Get(sessionId);
        bool isHost = session?.Host == sender;
        bool isClient = session?.Client == sender;

        if (session is null || (!isHost && !isClient) || (fromHostOnly == true && !isHost) || (fromHostOnly == false && !isClient)
            || (message is SdpSignal { SdpType: not ("offer" or "answer") }) || (message is SdpSignal { SdpType: "answer" } && !isClient))
        {
            await sender.SendAsync(new ErrorSignal("session_not_found", "세션이 없거나 권한이 없습니다.", sessionId), cancellationToken);
            return;
        }

        await (isHost ? session.Client : session.Host).SendAsync(message, cancellationToken);
    }

    private static bool VerifyHostSignature(string hostId, string publicKeyBase64, string nonce, string signatureBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.KeySize == 256
                && key.VerifyData(SignalingJson.HostAuthPayload(hostId, nonce), Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static bool IsValidHostId(string? hostId) =>
        hostId is { Length: >= 6 and <= 40 } && hostId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > SignalingJson.MaxMessageBytes)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "too big", cancellationToken);
                return null;
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }
}
