using Microsoft.AspNetCore.Builder;

namespace Signaling.Server;

public static class SignalingApp
{
    /// <summary>서버 구성. 테스트에서도 같은 구성으로 실제 Kestrel 서버를 띄웁니다.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.Services.Configure<SignalingOptions>(builder.Configuration.GetSection("Signaling"));
        builder.Services.AddSingleton<HostRegistry>();
        builder.Services.AddSingleton<SessionBroker>();
        builder.Services.AddSingleton<RateLimiter>();
        builder.Services.AddSingleton<IceServerProvider>();
        builder.Services.AddSingleton<SignalingHub>();

        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // 온라인 상태 조회: /api/status?ids=HOST-AAAAAA,HOST-BBBBBB (최대 50개)
        app.MapGet("/api/status", (string? ids, HttpContext context, HostRegistry registry, RateLimiter limiter) =>
        {
            if (!limiter.AllowStatus(context.Connection.RemoteIpAddress))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var hostIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50).Distinct();
            return Results.Ok(new { online = hostIds.ToDictionary(id => id, registry.IsOnline) });
        });

        app.Map("/ws", async (HttpContext context, SignalingHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(socket, context.Connection.RemoteIpAddress, context.RequestAborted);
        });

        return app;
    }
}
