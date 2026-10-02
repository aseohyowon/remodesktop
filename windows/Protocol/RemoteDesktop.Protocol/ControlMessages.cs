using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesktop.Protocol;

/// <summary>
/// JSON 제어 메시지의 기본 타입입니다. 직렬화 결과의 첫 속성은 항상 "type"입니다.
/// 예: {"type":"frame_ack","frame_id":42}
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(AuthChallengeMessage), "auth_challenge")]
[JsonDerivedType(typeof(AuthResponseMessage), "auth_response")]
[JsonDerivedType(typeof(AuthResultMessage), "auth_result")]
[JsonDerivedType(typeof(FrameAckMessage), "frame_ack")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(MouseMoveMessage), "mouse_move")]
[JsonDerivedType(typeof(MouseButtonMessage), "mouse_button")]
[JsonDerivedType(typeof(MouseWheelMessage), "mouse_wheel")]
[JsonDerivedType(typeof(KeyDownMessage), "key_down")]
[JsonDerivedType(typeof(KeyUpMessage), "key_up")]
[JsonDerivedType(typeof(TextInputMessage), "text_input")]
[JsonDerivedType(typeof(KeyframeRequestMessage), "keyframe_request")]
[JsonDerivedType(typeof(StreamStatsMessage), "stream_stats")]
[JsonDerivedType(typeof(SelectMonitorMessage), "select_monitor")]
[JsonDerivedType(typeof(ClipboardMessage), "clipboard")]
[JsonDerivedType(typeof(FileListRequestMessage), "file_list_request")]
[JsonDerivedType(typeof(FileListMessage), "file_list")]
[JsonDerivedType(typeof(FileDownloadMessage), "file_download")]
[JsonDerivedType(typeof(FileBeginMessage), "file_begin")]
[JsonDerivedType(typeof(FileEndMessage), "file_end")]
[JsonDerivedType(typeof(FileResultMessage), "file_result")]
[JsonDerivedType(typeof(FileCancelMessage), "file_cancel")]
[JsonDerivedType(typeof(AudioControlMessage), "audio")]
[JsonDerivedType(typeof(AudioFormatMessage), "audio_format")]
[JsonDerivedType(typeof(PowerActionMessage), "power_action")]
[JsonDerivedType(typeof(PowerResultMessage), "power_result")]
[JsonDerivedType(typeof(DisplayModesRequestMessage), "display_modes_request")]
[JsonDerivedType(typeof(DisplayModesMessage), "display_modes")]
[JsonDerivedType(typeof(SetResolutionMessage), "set_resolution")]
[JsonDerivedType(typeof(DisplayResultMessage), "display_result")]
[JsonDerivedType(typeof(SendSasMessage), "send_sas")]
[JsonDerivedType(typeof(SasResultMessage), "sas_result")]
public abstract record ControlMessage;

/// <summary>
/// Client → Host. 연결 직후 첫 메시지입니다.
/// codecs: Client가 디코딩할 수 있는 영상 코덱 (선호 순서). 없으면 ["jpeg"]
/// </summary>
public sealed record HelloMessage(
    int ProtocolVersion,
    string ClientName,
    string Platform,
    string ClientNonce,
    string[]? Codecs = null) : ControlMessage;

/// <summary>
/// Host → Client. 매 연결마다 새로 만든 server_nonce와 사용할 수 있는 인증 방식을 보냅니다.
/// auth_methods: "access_code" | "password" | "device"
/// </summary>
public sealed record AuthChallengeMessage(
    int ProtocolVersion,
    string HostId,
    string HostName,
    string ServerNonce,
    string[]? AuthMethods = null,
    string? PasswordSalt = null,
    int PasswordIterations = 0,
    bool TotpRequired = false) : ControlMessage;

/// <summary>
/// Client → Host. 비밀값 자체가 아니라 HMAC 증명값만 보냅니다.
/// totp: 2단계 인증 6자리 (access_code/password 방식이고 Host가 요구할 때만)
/// register_device: 인증 성공 시 이 기기를 신뢰된 장치로 등록 요청
/// </summary>
public sealed record AuthResponseMessage(
    string Proof,
    string Method = AuthMethods.AccessCode,
    string? DeviceId = null,
    string? Totp = null,
    bool RegisterDevice = false,
    string? DeviceName = null) : ControlMessage;

/// <summary>
/// Host → Client. 성공하면 Host도 같은 비밀을 안다는 증명(host_proof)을 보냅니다.
/// 장치 등록을 요청했으면 device_id/device_secret을 함께 보냅니다(한 번만 전달됨).
/// </summary>
public sealed record AuthResultMessage(
    bool Success,
    string? Error,
    string? SessionId,
    string? HostProof,
    int ScreenWidth,
    int ScreenHeight,
    string? DeviceId = null,
    string? DeviceSecret = null,
    string? ErrorCode = null,
    MonitorInfo[]? Monitors = null,
    string? VideoCodec = null,
    string[]? Features = null,
    string[]? MacAddresses = null) : ControlMessage;

/// <summary>Host의 모니터. index는 select_monitor에 사용 (0부터, -1 = 전체)</summary>
public sealed record MonitorInfo(int Index, int X, int Y, int Width, int Height, bool Primary);

/// <summary>auth_result.features: Host가 허용한 부가 기능</summary>
public static class HostFeatures
{
    public const string Input = "input";
    public const string Clipboard = "clipboard";
    public const string FileTransfer = "file_transfer";
    public const string Audio = "audio";
    public const string Power = "power";
    public const string MonitorSelect = "monitor_select";
    public const string Display = "display";

    /// <summary>Ctrl+Alt+Del 보내기 (STEP 13, Host가 Windows 서비스로 실행 중일 때만)</summary>
    public const string SecureAttention = "sas";
}

public static class AuthMethods
{
    public const string AccessCode = "access_code";
    public const string Password = "password";
    public const string Device = "device";
}

/// <summary>auth_result.error_code 값. Client가 다음 동작을 정할 때 씁니다.</summary>
public static class AuthErrorCodes
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string TotpRequired = "totp_required";
    public const string DeviceRevoked = "device_revoked";
    public const string Denied = "denied";
    public const string Busy = "busy";
    public const string Locked = "locked";
    public const string Unsupported = "unsupported";
}

/// <summary>Client → Host. 프레임을 화면에 표시했음을 알립니다. Host는 이것으로 전송 속도를 조절합니다.</summary>
public sealed record FrameAckMessage(uint FrameId) : ControlMessage;

/// <summary>양방향. 정상 종료 알림입니다.</summary>
public sealed record ByeMessage(string Reason) : ControlMessage;

public enum MouseButton
{
    Left,
    Right,
    Middle,
    X1,
    X2
}

public enum ButtonAction
{
    Down,
    Up
}

/// <summary>Client → Host. 좌표는 Host 화면 기준 0~1 정규화 값입니다. (0,0)=왼쪽 위, (1,1)=오른쪽 아래</summary>
public sealed record MouseMoveMessage(double X, double Y) : ControlMessage;

/// <summary>Client → Host. 클릭 = down + up, 더블 클릭 = 클릭 두 번, 드래그 = down → move... → up</summary>
public sealed record MouseButtonMessage(MouseButton Button, ButtonAction Action) : ControlMessage;

/// <summary>Client → Host. 120 = 휠 한 칸. delta_y 양수 = 위로, delta_x 양수 = 오른쪽.</summary>
public sealed record MouseWheelMessage(int DeltaX, int DeltaY) : ControlMessage;

/// <summary>Client → Host. code는 W3C KeyboardEvent.code 이름입니다. 예: "KeyA", "ControlLeft", "F5", "Lang1"(한/영)</summary>
public sealed record KeyDownMessage(string Code) : ControlMessage;

public sealed record KeyUpMessage(string Code) : ControlMessage;

/// <summary>Client → Host. 완성된 문자열 입력(모바일 가상 키보드, 한글 조합 결과 등). 최대 256자.</summary>
public sealed record TextInputMessage(string Text) : ControlMessage;

// ---------------- STEP 8: 성능 ----------------

/// <summary>Client → Host. 디코딩 오류 등으로 다음 프레임을 키프레임으로 요청</summary>
public sealed record KeyframeRequestMessage() : ControlMessage;

/// <summary>Host → Client (2초마다). 연결 품질 표시용</summary>
public sealed record StreamStatsMessage(
    double Fps,
    int Kbps,
    int RttMs,
    int QualityLevel,
    string Codec,
    int Width,
    int Height,
    string? Encoder = null) : ControlMessage;

// ---------------- STEP 9: 고급 기능 ----------------

/// <summary>Client → Host. 볼 모니터 선택 (-1 = 전체 모니터)</summary>
public sealed record SelectMonitorMessage(int Index) : ControlMessage;

/// <summary>양방향. 텍스트 클립보드 (최대 32,000자)</summary>
public sealed record ClipboardMessage(string Text) : ControlMessage;

/// <summary>Client → Host. Host 공유 폴더의 파일 목록 요청</summary>
public sealed record FileListRequestMessage() : ControlMessage;

public sealed record FileEntry(string Name, long Size, DateTimeOffset Modified);

/// <summary>Host → Client</summary>
public sealed record FileListMessage(string Folder, FileEntry[] Files) : ControlMessage;

/// <summary>Client → Host. 공유 폴더의 파일 받기</summary>
public sealed record FileDownloadMessage(string Name) : ControlMessage;

/// <summary>
/// 양방향. 파일 전송 시작. 이어서 file_chunk(바이너리, kind 3)가 오고 file_end로 끝납니다.
/// direction: "upload"(Client → Host) | "download"(Host → Client)
/// </summary>
public sealed record FileBeginMessage(uint TransferId, string Name, long Size, string Direction) : ControlMessage;

/// <summary>양방향. 전송 끝. sha256: 전체 파일의 SHA-256 (16진수)</summary>
public sealed record FileEndMessage(uint TransferId, string Sha256) : ControlMessage;

/// <summary>받는 쪽 → 보낸 쪽. 저장 결과</summary>
public sealed record FileResultMessage(uint TransferId, bool Success, string? Error = null, string? SavedAs = null) : ControlMessage;

public sealed record FileCancelMessage(uint TransferId, string? Reason = null) : ControlMessage;

/// <summary>Client → Host. action: "start" | "stop"</summary>
public sealed record AudioControlMessage(string Action) : ControlMessage;

/// <summary>Host → Client. 이어서 오는 오디오 데이터(kind 4)의 형식. codec: "pcm16"</summary>
public sealed record AudioFormatMessage(string Codec, int SampleRate, int Channels) : ControlMessage;

/// <summary>Client → Host. action: "lock" | "logoff" | "restart" | "shutdown"</summary>
public sealed record PowerActionMessage(string Action) : ControlMessage;

public sealed record PowerResultMessage(string Action, bool Success, string? Error = null) : ControlMessage;

// ---------------- STEP 12: 해상도 ----------------

/// <summary>Client → Host. 지금 보고 있는 모니터에서 쓸 수 있는 해상도 목록 요청</summary>
public sealed record DisplayModesRequestMessage() : ControlMessage;

public sealed record DisplayMode(int Width, int Height);

/// <summary>Host → Client. current: 지금 해상도, original: 세션 시작 전 해상도(되돌리기용)</summary>
public sealed record DisplayModesMessage(DisplayMode Current, DisplayMode Original, DisplayMode[] Modes) : ControlMessage;

/// <summary>Client → Host. 해상도 변경. width/height가 0이면 원래 해상도로 되돌림</summary>
public sealed record SetResolutionMessage(int Width, int Height) : ControlMessage;

public sealed record DisplayResultMessage(bool Success, DisplayMode? Current = null, string? Error = null) : ControlMessage;

// ---------------- STEP 13: 보안 화면 ----------------

/// <summary>Client → Host. Ctrl+Alt+Del (Secure Attention Sequence). 기능 "sas"가 있을 때만</summary>
public sealed record SendSasMessage() : ControlMessage;

public sealed record SasResultMessage(bool Success, string? Error = null) : ControlMessage;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    public static byte[] Serialize(ControlMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <summary>알 수 없는 type이면 null을 반환합니다. (새 버전 Client와의 호환용)</summary>
    public static ControlMessage? Deserialize(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(json, Options)
                ?? throw new ProtocolException("빈 제어 메시지입니다.");
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (JsonException exception)
        {
            throw new ProtocolException($"잘못된 JSON 제어 메시지입니다: {exception.Message}");
        }
    }
}
