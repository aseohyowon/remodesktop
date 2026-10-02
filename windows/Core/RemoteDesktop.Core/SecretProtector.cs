using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RemoteDesktop.Core;

/// <summary>
/// Windows DPAPI(현재 사용자 범위)로 비밀값을 암호화해 파일에 저장할 수 있게 합니다.
/// 같은 Windows 사용자 계정으로 로그인해야만 복호화할 수 있습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretProtector
{
    private static readonly byte[] Entropy = "RemoteDesktop/secret/v1"u8.ToArray();

    public static string Protect(byte[] secret) =>
        Convert.ToBase64String(ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser));

    public static byte[] Unprotect(string protectedBase64) =>
        ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
}
