using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>전원 동작 (테스트에서 실제로 잠그거나 재부팅하지 않도록 분리)</summary>
public interface IPowerController
{
    void Execute(string action);
}

/// <summary>
/// 실제 Windows 전원 동작
/// - 잠금: LockWorkStation (항상 허용)
/// - 로그아웃: ExitWindowsEx(EWX_LOGOFF)
/// - 재시작/종료: shutdown.exe /r(/s) /t 5 — 5초 뒤 실행되어 결과 메시지를 보낼 시간이 있습니다.
/// </summary>
public sealed class WindowsPowerController : IPowerController
{
    private readonly bool _sessionAgent;

    /// <param name="sessionAgent">
    /// 서비스 모드 에이전트(STEP 13, SYSTEM 권한): 로그아웃은 ExitWindowsEx 대신 WTSLogoffSession으로
    /// 에이전트가 떠 있는 사용자 세션을 로그아웃합니다. (SYSTEM의 ExitWindowsEx는 사용자 세션을 대상으로 하지 않음)
    /// </param>
    public WindowsPowerController(bool sessionAgent = false)
    {
        _sessionAgent = sessionAgent;
    }

    public void Execute(string action)
    {
        switch (action)
        {
            case "lock":
                if (!LockWorkStation())
                {
                    throw new InvalidOperationException($"화면 잠금 실패 (Win32 오류 {Marshal.GetLastWin32Error()})");
                }

                break;
            case "logoff" when _sessionAgent:
                if (!WTSLogoffSession(IntPtr.Zero, (uint)Process.GetCurrentProcess().SessionId, false))
                {
                    throw new InvalidOperationException($"로그아웃 실패 (Win32 오류 {Marshal.GetLastWin32Error()})");
                }

                break;
            case "logoff":
                if (!ExitWindowsEx(0 /* EWX_LOGOFF */, 0))
                {
                    throw new InvalidOperationException($"로그아웃 실패 (Win32 오류 {Marshal.GetLastWin32Error()})");
                }

                break;
            case "restart":
                RunShutdown("/r");
                break;
            case "shutdown":
                RunShutdown("/s");
                break;
            default:
                throw new ArgumentException("알 수 없는 동작입니다.");
        }
    }

    private static void RunShutdown(string mode)
    {
        using var process = Process.Start(new ProcessStartInfo("shutdown.exe", $"{mode} /t 5 /c \"Remote Desktop 원격 요청\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("shutdown.exe를 실행할 수 없습니다.");
        process.WaitForExit(5000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"shutdown.exe 실패 (코드 {process.ExitCode})");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr server, uint sessionId, bool wait);
}

/// <summary>
/// 시스템 소리 전송: WASAPI 루프백으로 스피커 출력을 캡처 → 16bit PCM → 오디오 메시지(kind 4)
/// 48 kHz 스테레오 기준 약 1.5 Mbps입니다. (LAN 권장, 인터넷은 대역폭에 주의)
/// </summary>
internal sealed class AudioStreamer : IDisposable
{
    private readonly MessageChannel _channel;
    private readonly CancellationToken _cancellationToken;
    private const int MaxPendingSends = 8; // 약 80~160 ms 분량
    private WasapiLoopbackCapture? _capture;
    private uint _sequence;
    private int _pendingSends;

    public AudioStreamer(MessageChannel channel, CancellationToken cancellationToken)
    {
        _channel = channel;
        _cancellationToken = cancellationToken;
    }

    public bool IsRunning => _capture is not null;

    public async Task StartAsync()
    {
        if (_capture is not null)
        {
            return;
        }

        var capture = new WasapiLoopbackCapture();
        WaveFormat format = capture.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
        {
            capture.Dispose();
            throw new NotSupportedException($"지원하지 않는 오디오 형식입니다: {format}");
        }

        await _channel.SendControlAsync(new AudioFormatMessage("pcm16", format.SampleRate, format.Channels), _cancellationToken);
        capture.DataAvailable += OnData;
        capture.StartRecording();
        _capture = capture;
        Log.Info($"Audio started: {format.SampleRate} Hz, {format.Channels} ch");
    }

    public void Stop()
    {
        if (_capture is { } capture)
        {
            _capture = null;
            capture.DataAvailable -= OnData;
            try
            {
                capture.StopRecording();
            }
            catch
            {
            }

            capture.Dispose();
            Log.Info("Audio stopped");
        }
    }

    /// <summary>float32 → int16 변환 후 전송. 오디오 스레드에서 호출됩니다.</summary>
    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // 네트워크가 느려 전송이 밀리면 지연이 계속 커지므로, 밀린 동안의 소리는 버립니다.
        if (Volatile.Read(ref _pendingSends) >= MaxPendingSends)
        {
            return;
        }

        int samples = e.BytesRecorded / 4;
        byte[] pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            float value = BitConverter.ToSingle(e.Buffer, i * 4);
            short sample = (short)Math.Clamp(value * 32767f, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample);
        }

        byte[] head = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(head, _sequence++);
        Interlocked.Increment(ref _pendingSends);
        _ = _channel.SendBinaryAsync(MessageKind.Audio, head, pcm, _cancellationToken)
            .ContinueWith(_ => Interlocked.Decrement(ref _pendingSends), TaskScheduler.Default);
    }

    public void Dispose() => Stop();
}
