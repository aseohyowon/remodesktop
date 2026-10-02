namespace RemoteDesktop.Core;

/// <summary>
/// 사용자별 설정 파일 위치입니다. 예: C:\Users\사용자\AppData\Local\RemoteDesktop
/// 환경 변수 REMODESK_DATA_DIR로 바꿀 수 있습니다(테스트, 휴대용 실행).
/// </summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            string path = Environment.GetEnvironmentVariable("REMODESK_DATA_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesktop");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string GetFile(string fileName) => Path.Combine(DataDirectory, fileName);
}
