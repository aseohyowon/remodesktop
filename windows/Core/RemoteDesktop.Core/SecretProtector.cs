using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RemoteDesktop.Core;

/// <summary>
/// Windows DPAPI로 비밀값을 암호화해 파일에 저장할 수 있게 합니다.
/// - 기본(현재 사용자 범위): 같은 Windows 사용자 계정으로 로그인해야만 복호화할 수 있습니다.
/// - 환경 변수 REMODESK_DPAPI_SCOPE=machine (STEP 13 Windows 서비스): 이 PC 범위.
///   서비스(SYSTEM)와 관리자 명령이 같은 설정을 읽어야 하므로 이 범위를 쓰고,
///   대신 데이터 폴더(C:\ProgramData\RemoteDesktop)를 SYSTEM과 Administrators만 읽을 수 있게 막습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretProtector
{
    private static readonly byte[] Entropy = "RemoteDesktop/secret/v1"u8.ToArray();

    public static DataProtectionScope Scope =>
        string.Equals(Environment.GetEnvironmentVariable("REMODESK_DPAPI_SCOPE"), "machine", StringComparison.OrdinalIgnoreCase)
            ? DataProtectionScope.LocalMachine
            : DataProtectionScope.CurrentUser;

    public static string Protect(byte[] secret) =>
        Convert.ToBase64String(ProtectedData.Protect(secret, Entropy, Scope));

    public static byte[] Unprotect(string protectedBase64) =>
        ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, Scope);
}
