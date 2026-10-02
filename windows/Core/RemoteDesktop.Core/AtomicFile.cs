namespace RemoteDesktop.Core;

public static class AtomicFile
{
    /// <summary>
    /// 임시 파일에 쓴 뒤 교체합니다. 쓰는 도중 꺼져도 기존 파일이 깨지지 않습니다.
    /// 임시 파일 이름은 매번 달라서 여러 스레드/프로세스가 동시에 저장해도 서로 부딪히지 않습니다.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
