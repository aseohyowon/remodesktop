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

    /// <summary>Windows 서비스 모드(STEP 13)의 데이터 폴더: C:\ProgramData\RemoteDesktop</summary>
    public static string ServiceDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktop");

    /// <summary>
    /// 이 프로세스(와 자식 프로세스)가 서비스 데이터 폴더와 PC 범위 DPAPI를 쓰게 합니다.
    /// 이미 REMODESK_DATA_DIR이 있으면(테스트) 그대로 둡니다.
    /// </summary>
    public static void UseServiceData()
    {
        if (Environment.GetEnvironmentVariable("REMODESK_DATA_DIR") is not { Length: > 0 })
        {
            Environment.SetEnvironmentVariable("REMODESK_DATA_DIR", ServiceDataDirectory);
        }

        Environment.SetEnvironmentVariable("REMODESK_DPAPI_SCOPE", "machine");
    }
}
