using System.Text.Json;
using RemoteDesktop.Core;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 접속 기록: %LOCALAPPDATA%\RemoteDesktop\access-log.jsonl (한 줄에 JSON 하나)
/// 비밀번호, 접속 코드, 증명값, 장치 비밀키는 기록하지 않습니다.
/// </summary>
public static class AccessLog
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>테스트에서 실제 사용자 파일 대신 임시 파일을 쓰기 위한 경로</summary>
    public static string? PathOverride { get; set; }

    public static string FilePath => PathOverride ?? AppPaths.GetFile("access-log.jsonl");

    public static void Write(string evt, string remote, string? clientName = null, string? method = null, string? detail = null)
    {
        var entry = new Entry(DateTimeOffset.Now, evt, remote, clientName, method, detail);
        string line = JsonSerializer.Serialize(entry, JsonOptions);

        lock (Sync)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true); // 간단한 로그 순환
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException exception)
            {
                Log.Warn($"접속 기록을 쓰지 못했습니다: {exception.Message}");
            }
        }
    }

    public static IReadOnlyList<string> ReadLast(int count)
    {
        lock (Sync)
        {
            return File.Exists(FilePath) ? File.ReadLines(FilePath).TakeLast(count).ToList() : [];
        }
    }

    private sealed record Entry(DateTimeOffset Time, string Event, string Remote, string? Client, string? Method, string? Detail);
}
