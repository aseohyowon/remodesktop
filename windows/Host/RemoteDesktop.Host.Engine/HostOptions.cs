using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

public sealed record HostOptions
{
    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    /// <summary>보낼 모니터 번호(1부터). null이면 주 모니터</summary>
    public int? Monitor { get; init; }

    public int MaxFps { get; init; } = 30;

    public int JpegQuality { get; init; } = 70;

    public bool ViewOnly { get; init; }

    public bool Verbose { get; init; }

    /// <summary>LAN 직접 연결(TCP) 대기. false면 시그널링(인터넷) 연결만 받습니다.</summary>
    public bool EnableLan { get; init; } = true;

    /// <summary>
    /// 기본값 false: 사설망(192.168.x.x, 10.x.x.x 등)과 이 PC에서 오는 연결만 받습니다.
    /// 공인 IP에서 오는 직접 연결은 거부합니다. (인터넷 원격은 STEP 7의 시그널링/WebRTC 사용)
    /// </summary>
    public bool AllowPublicAddresses { get; init; }

    /// <summary>실행할 때마다 바뀌는 접속 코드 허용</summary>
    public bool AccessCodeEnabled { get; init; } = true;

    /// <summary>신뢰된 장치가 아닌 접속은 Host 사용자 승인 필요</summary>
    public bool RequireApproval { get; init; }

    /// <summary>한 세션의 최대 시간. 지나면 연결을 끊고 다시 인증해야 합니다.</summary>
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromHours(12);

    /// <summary>시그널링 서버 주소 (예: wss://signal.example.com/ws). null이면 인터넷 연결 사용 안 함 (STEP 7)</summary>
    public Uri? SignalingServer { get; init; }

    /// <summary>영상 코덱: auto(Client가 지원하면 H.264) | h264 | jpeg (STEP 8)</summary>
    public string Codec { get; init; } = "auto";

    /// <summary>H.264 최대 비트레이트 (kbps)</summary>
    public int MaxBitrateKbps { get; init; } = 8000;

    /// <summary>네트워크 상태에 따라 해상도/FPS/화질 자동 조절</summary>
    public bool AdaptiveQuality { get; init; } = true;

    /// <summary>GPU 하드웨어 인코더 우선 사용 (없으면 CPU)</summary>
    public bool PreferHardwareEncoder { get; init; } = true;

    /// <summary>클립보드 공유 (STEP 9)</summary>
    public bool AllowClipboard { get; init; } = true;

    /// <summary>파일 전송 (STEP 9). 공유 폴더 안에서만 주고받습니다.</summary>
    public bool AllowFileTransfer { get; init; } = true;

    /// <summary>파일 전송에 쓰는 폴더. 기본: %USERPROFILE%\RemoteDesktop</summary>
    public string SharedFolder { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RemoteDesktop");

    /// <summary>시스템 소리 전송 (STEP 9)</summary>
    public bool AllowAudio { get; init; } = true;

    /// <summary>원격 로그아웃/재부팅/종료 허용 (STEP 9). 화면 잠금은 항상 허용됩니다.</summary>
    public bool AllowPower { get; init; }

    /// <summary>Client가 볼 모니터를 바꿀 수 있음 (STEP 9)</summary>
    public bool AllowMonitorSelect { get; init; } = true;

    /// <summary>Client가 이 PC의 해상도를 바꿀 수 있음 (STEP 12). 연결이 끝나면 원래대로 돌아갑니다.</summary>
    public bool AllowResolutionChange { get; init; } = true;

    /// <summary>같은 네트워크의 Client가 이 PC를 자동으로 찾을 수 있음 (STEP 11, UDP)</summary>
    public bool Discoverable { get; init; } = true;

    public int DiscoveryPort { get; init; } = RemoteDesktop.Protocol.LanDiscovery.DefaultPort;

    /// <summary>
    /// 입력 데스크톱(로그인/잠금/UAC 화면 포함)을 따라가며 캡처·입력 (STEP 13).
    /// Windows 서비스가 SYSTEM 권한으로 띄운 에이전트에서 켭니다. 명령줄 옵션은 없습니다.
    /// </summary>
    public bool FollowInputDesktop { get; init; }

    public static HostOptions Parse(string[] args)
    {
        var options = new HostOptions();

        for (int i = 0; i < args.Length; i++)
        {
            options = args[i] switch
            {
                "--port" => options with { Port = ReadInt(args, ref i, 1024, 65535) },
                "--monitor" => options with { Monitor = ReadInt(args, ref i, 1, 16) },
                "--fps" => options with { MaxFps = ReadInt(args, ref i, 1, 60) },
                "--quality" => options with { JpegQuality = ReadInt(args, ref i, 10, 95) },
                "--view-only" => options with { ViewOnly = true },
                "--verbose" => options with { Verbose = true },
                "--no-lan" => options with { EnableLan = false },
                "--allow-public" => options with { AllowPublicAddresses = true },
                "--no-access-code" => options with { AccessCodeEnabled = false },
                "--require-approval" => options with { RequireApproval = true },
                "--session-hours" => options with { MaxSessionDuration = TimeSpan.FromHours(ReadInt(args, ref i, 1, 168)) },
                "--signal" => options with { SignalingServer = ReadUri(args, ref i) },
                "--codec" => options with { Codec = ReadChoice(args, ref i, "auto", "h264", "jpeg") },
                "--bitrate" => options with { MaxBitrateKbps = ReadInt(args, ref i, 300, 50000) },
                "--no-adaptive" => options with { AdaptiveQuality = false },
                "--cpu-encoder" => options with { PreferHardwareEncoder = false },
                "--no-clipboard" => options with { AllowClipboard = false },
                "--no-file-transfer" => options with { AllowFileTransfer = false },
                "--shared-folder" => options with { SharedFolder = ReadString(args, ref i) },
                "--no-audio" => options with { AllowAudio = false },
                "--allow-power" => options with { AllowPower = true },
                "--no-discovery" => options with { Discoverable = false },
                "--no-resolution-change" => options with { AllowResolutionChange = false },
                _ => throw new ArgumentException($"알 수 없는 옵션입니다: {args[i]}")
            };
        }

        return options;
    }

    private static int ReadInt(string[] args, ref int index, int min, int max)
    {
        string name = args[index];
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value < min || value > max)
        {
            throw new ArgumentException($"{name} 값은 {min}~{max} 사이의 숫자여야 합니다.");
        }

        index++;
        return value;
    }

    private static string ReadString(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{args[index]} 값이 필요합니다.");
        }

        return args[++index];
    }

    private static string ReadChoice(string[] args, ref int index, params string[] choices)
    {
        string name = args[index];
        string value = ReadString(args, ref index).ToLowerInvariant();
        return choices.Contains(value) ? value : throw new ArgumentException($"{name} 값은 {string.Join(", ", choices)} 중 하나여야 합니다.");
    }

    private static Uri ReadUri(string[] args, ref int index)
    {
        string name = args[index];
        if (index + 1 >= args.Length
            || !Uri.TryCreate(args[index + 1], UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException($"{name} 값은 ws:// 또는 wss:// 주소여야 합니다.");
        }

        index++;
        return uri;
    }
}
