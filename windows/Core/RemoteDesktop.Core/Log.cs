namespace RemoteDesktop.Core;

/// <summary>
/// 개발 단계용 콘솔 로그입니다.
/// 비밀번호, 접속 코드, 인증 증명값, 토큰은 절대 이 클래스로 기록하지 않습니다.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();

    public static bool DebugEnabled { get; set; }

    public static void Debug(string message)
    {
        if (DebugEnabled)
        {
            Write("DEBUG", message, ConsoleColor.DarkGray);
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, ConsoleColor.Yellow);

    public static void Error(string message) => Write("ERROR", message, ConsoleColor.Red);

    /// <summary>환경 변수 REMODESK_LOG_FILE이 있으면 그 파일에도 기록합니다. (문제 진단용)</summary>
    private static string? LogFile = Environment.GetEnvironmentVariable("REMODESK_LOG_FILE");

    /// <summary>
    /// 로그 파일 지정 (창 앱은 콘솔이 없으므로 파일로 남깁니다). 5 MB를 넘으면 .1로 옮기고 새로 씁니다.
    /// 환경 변수 REMODESK_LOG_FILE이 있으면 그것을 우선합니다.
    /// </summary>
    public static void SetFile(string path)
    {
        if (Environment.GetEnvironmentVariable("REMODESK_LOG_FILE") is { Length: > 0 })
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 5 * 1024 * 1024)
            {
                File.Move(path, path + ".1", overwrite: true);
            }
        }
        catch (IOException)
        {
        }

        LogFile = path;
    }

    private static void Write(string level, string message, ConsoleColor? color)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        lock (Sync)
        {
            if (LogFile is not null)
            {
                try
                {
                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
                catch (IOException)
                {
                }
            }

            if (color is { } value)
            {
                Console.ForegroundColor = value;
                Console.WriteLine(line);
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }
}
