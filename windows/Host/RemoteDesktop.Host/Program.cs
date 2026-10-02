using System.Text;
using RemoteDesktop.Core;
using RemoteDesktop.Host.Engine;

namespace RemoteDesktop.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Host는 Windows에서만 실행할 수 있습니다.");
            return 1;
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // 화면 배율(125%, 150% 등)이 있어도 실제 픽셀 좌표로 캡처하도록 가장 먼저 설정합니다.
        NativeMethods.EnablePerMonitorDpiAwareness();

        try
        {
            return args switch
            {
                ["capture", .. var rest] => StepOneCapture.Run(rest),
                ["password", .. var rest] => HostCommands.Password(rest),
                ["devices", .. var rest] => HostCommands.Devices(rest),
                ["totp", .. var rest] => HostCommands.TotpCommand(rest),
                ["log", .. var rest] => HostCommands.ShowLog(rest),
                ["-h"] or ["--help"] or ["help"] => PrintUsage(0),
                _ => await RunHostAsync(args)
            };
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return PrintUsage(1);
        }
    }

    private static async Task<int> RunHostAsync(string[] args)
    {
        HostOptions options = HostOptions.Parse(args);
        Log.DebugEnabled = options.Verbose;

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            var callbacks = new ConsoleHostCallbacks();
            var server = new HostServer(options, HostSettings.Load(), callbacks);
            PrintConnectionInfo(server);
            await server.RunAsync(shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is not ArgumentException)
        {
            Log.Error($"Host 실행 실패: {exception.Message}");
            return 1;
        }
        finally
        {
            Log.Info("Host stopped");
        }
    }

    private static void PrintConnectionInfo(HostServer server)
    {
        // 접속 코드는 화면에만 보여 주고 Log(로그)로는 남기지 않습니다.
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine($"  Host ID     : {server.Settings.HostId}");
        if (server.Options.AccessCodeEnabled)
        {
            Console.WriteLine($"  접속 코드   : {server.FormattedAccessCode}   (Host를 다시 시작하면 바뀝니다)");
        }

        if (server.Settings.HasPassword)
        {
            Console.WriteLine("  비밀번호    : 설정됨");
        }

        if (server.Options.EnableLan)
        {
            Console.WriteLine($"  LAN 주소    : {string.Join(", ", HostServer.GetLanAddresses().Select(a => $"{a}:{server.Options.Port}"))}");
        }

        if (server.Options.SignalingServer is { } signal)
        {
            Console.WriteLine($"  인터넷      : {signal} 에 {server.Settings.HostId}로 등록");
        }

        Console.WriteLine($"  인증서 지문 : {RemoteDesktop.Protocol.AuthProof.FormatFingerprint(server.TlsChannelBinding)}");
        Console.WriteLine("============================================================");
        Console.WriteLine();
    }

    private static int PrintUsage(int exitCode)
    {
        Console.WriteLine("""
            사용법:
              RemoteDesktop.Host.exe [옵션]                 Host 실행
              RemoteDesktop.Host.exe capture [파일.bmp]     STEP 1 화면 저장
              RemoteDesktop.Host.exe password set|clear     접속 비밀번호 설정/삭제
              RemoteDesktop.Host.exe devices [revoke <ID|all>]  신뢰된 장치 목록/해제
              RemoteDesktop.Host.exe totp enable|disable    2단계 인증(TOTP) 켜기/끄기
              RemoteDesktop.Host.exe log [개수]             접속 기록 보기

            옵션:
              --port 50505         LAN 포트
              --monitor 1          보낼 모니터 번호 (생략하면 주 모니터)
              --fps 30             최대 초당 프레임 (1~60)
              --quality 70         JPEG 품질 (10~95)
              --view-only          화면만 보여 주고 입력은 받지 않음
              --require-approval   신뢰된 장치가 아닌 접속은 Host에서 승인 필요
              --no-access-code     접속 코드 끄기 (비밀번호/신뢰된 장치만 허용)
              --session-hours 12   세션 최대 시간
              --allow-public       공인 IP의 직접 LAN 연결 허용 (권장하지 않음)
              --signal wss://...   인터넷 연결용 시그널링 서버
              --no-lan             LAN 직접 연결 끄기
              --codec auto         영상 코덱: auto(Client가 지원하면 H.264) | h264 | jpeg
              --bitrate 8000       H.264 최대 비트레이트 (kbps)
              --no-adaptive        네트워크에 따른 자동 화질 조절 끄기
              --cpu-encoder        GPU 대신 CPU H.264 인코더 사용
              --no-clipboard       클립보드 공유 끄기
              --no-file-transfer   파일 전송 끄기
              --shared-folder 경로 파일 전송 폴더 (기본: %USERPROFILE%\RemoteDesktop)
              --no-audio           소리 전송 끄기
              --allow-power        원격 로그아웃/재시작/종료 허용 (잠금은 항상 허용)
              --no-discovery       같은 네트워크 자동 검색에 응답하지 않음
              --no-resolution-change  Client가 이 PC의 해상도를 바꾸지 못하게 함
              --verbose            상세 로그

            Windows 창 앱(RemoteDesktop.exe)을 쓰면 위 설정을 화면에서 바꿀 수 있습니다.
            """);
        return exitCode;
    }
}
