using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Core;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 에이전트 → 서비스 Ctrl+Alt+Del 요청 통로 (STEP 13, 이름 있는 파이프).
///
/// Ctrl+Alt+Del(SAS)은 키 입력으로 만들 수 없고, SendSAS API는 "서비스"만 호출할 수 있습니다.
/// 그래서 에이전트가 파이프로 서비스에 부탁하고, 서비스가 SendSAS를 호출합니다.
///
/// 보안:
/// - PipeOptions.CurrentUserOnly: 서비스와 같은 계정(SYSTEM)만 연결 가능
/// - 연결한 프로세스 ID가 서비스가 띄운 에이전트인지 확인
/// - 요청은 "sas" 한 가지뿐이고 2초에 한 번만
/// </summary>
public sealed class SasPipeServer
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly Func<int?> _allowedProcessId;
    private readonly Func<string?> _sendSas;
    private DateTime _lastSent = DateTime.MinValue;

    /// <param name="sendSas">성공하면 null, 실패하면 오류 문구</param>
    public SasPipeServer(string pipeName, Func<int?> allowedProcessId, Func<string?> sendSas)
    {
        _pipeName = pipeName;
        _allowedProcessId = allowedProcessId;
        _sendSas = sendSas;
    }

    public int Requests { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 256, 256);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandleAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                Log.Debug($"SAS pipe: {exception.Message}");
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        // 요청을 먼저 읽고(최대 16자) 보낸 프로세스를 확인한 뒤 답합니다.
        char[] buffer = new char[16];
        int read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token);
        string request = new string(buffer, 0, read).Trim();

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientId) || clientId != _allowedProcessId())
        {
            Log.Warn($"SAS pipe: rejected process {clientId} (서비스가 띄운 에이전트가 아님)");
            await writer.WriteLineAsync("error 허용되지 않은 프로세스입니다.".AsMemory(), timeout.Token);
            return;
        }

        if (request != "sas")
        {
            await writer.WriteLineAsync("error 알 수 없는 요청입니다.".AsMemory(), timeout.Token);
            return;
        }

        Requests++;
        string? error;
        if (DateTime.UtcNow - _lastSent < MinInterval)
        {
            error = "잠시 후 다시 시도하세요.";
        }
        else
        {
            _lastSent = DateTime.UtcNow;
            error = _sendSas();
        }

        await writer.WriteLineAsync((error is null ? "ok" : $"error {error}").AsMemory(), timeout.Token);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

public static class SasPipeClient
{
    /// <summary>서비스에 Ctrl+Alt+Del을 요청합니다. 성공하면 null, 실패하면 오류 문구</summary>
    public static async Task<string?> RequestAsync(string pipeName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);

        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync("sas".AsMemory(), timeout.Token);
        string reply = await reader.ReadLineAsync(timeout.Token) ?? "error 서비스가 응답하지 않았습니다.";
        return reply == "ok" ? null : reply.StartsWith("error ", StringComparison.Ordinal) ? reply[6..] : "알 수 없는 응답";
    }
}

/// <summary>서비스에서 Ctrl+Alt+Del 보내기</summary>
public static class SecureAttentionSequence
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>
    /// Windows 정책 SoftwareSASGeneration: 1(서비스) 또는 3(서비스+접근성 앱)이어야 SendSAS가 동작합니다.
    /// 설치 스크립트(install-service.ps1)가 1로 설정합니다.
    /// </summary>
    public static bool PolicyAllowsServices()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PolicyKey);
        return key?.GetValue("SoftwareSASGeneration") is int value && (value & 1) == 1;
    }

    public static string? Send()
    {
        if (!PolicyAllowsServices())
        {
            return "Windows 정책(SoftwareSASGeneration)이 꺼져 있어 Ctrl+Alt+Del을 보낼 수 없습니다. install-service.ps1을 다시 실행하세요.";
        }

        SendSAS(false); // false = 서비스가 호출 (결과값 없음)
        Log.Info("SendSAS called");
        return null;
    }

    [DllImport("sas.dll")]
    private static extern void SendSAS(bool asUser);
}
