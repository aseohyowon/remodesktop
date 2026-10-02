using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Protocol;

public enum AuthRole
{
    Client,
    Host
}

/// <summary>
/// 상호 인증 증명값 (docs/protocol.md 5장)
///
/// key   = 인증 방식별 키 (아래 Derive* 함수)
/// proof = HMAC-SHA256(key, "remodesktop-auth-v1:" + role + client_nonce + server_nonce + channel_binding)
///
/// - 비밀값(접속 코드, 비밀번호, 장치 비밀키)은 네트워크로 보내지 않습니다.
/// - 연결마다 새 nonce를 쓰므로 이전 proof를 재전송(replay)해도 통과하지 못합니다.
/// - channel_binding(TLS 인증서 해시 또는 WebRTC DTLS 지문 해시)을 섞으므로
///   중간자가 다른 암호화 연결로 중계하면 검증이 실패합니다.
/// </summary>
public static class AuthProof
{
    public const int KeyBytes = 32;

    public static string NormalizeAccessCode(string accessCode)
    {
        var builder = new StringBuilder(accessCode.Length);
        foreach (char character in accessCode)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>access_code 방식 키. 접속 코드는 Host 실행마다 바뀌는 일회성 값입니다.</summary>
    public static byte[] DeriveAccessCodeKey(string accessCode) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("remodesktop-access-code-v1:" + NormalizeAccessCode(accessCode)));

    /// <summary>password 방식 키. PBKDF2-HMAC-SHA256 (salt와 반복 횟수는 Host가 auth_challenge로 알려 줌)</summary>
    public static byte[] DerivePasswordKey(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    public static byte[] Compute(
        AuthRole role,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        ReadOnlySpan<byte> channelBinding)
    {
        byte[] label = Encoding.UTF8.GetBytes(
            role == AuthRole.Client ? "remodesktop-auth-v1:client" : "remodesktop-auth-v1:host");

        byte[] data = new byte[label.Length + clientNonce.Length + serverNonce.Length + channelBinding.Length];
        var span = data.AsSpan();
        label.CopyTo(span);
        span = span[label.Length..];
        clientNonce.CopyTo(span);
        span = span[clientNonce.Length..];
        serverNonce.CopyTo(span);
        span = span[serverNonce.Length..];
        channelBinding.CopyTo(span);

        return HMACSHA256.HashData(key, data);
    }

    public static bool Verify(
        AuthRole role,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        ReadOnlySpan<byte> channelBinding,
        string? proofBase64)
    {
        if (string.IsNullOrEmpty(proofBase64))
        {
            return false;
        }

        Span<byte> proof = stackalloc byte[64];
        if (!Convert.TryFromBase64String(proofBase64, proof, out int written) || written != 32)
        {
            return false;
        }

        byte[] expected = Compute(role, key, clientNonce, serverNonce, channelBinding);
        return CryptographicOperations.FixedTimeEquals(expected, proof[..written]);
    }

    public static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(ProtocolConstants.NonceBytes);

    public static bool TryDecodeNonce(string? base64, out byte[] nonce) => TryDecodeFixed(base64, ProtocolConstants.NonceBytes, out nonce);

    public static bool TryDecodeFixed(string? base64, int length, out byte[] bytes)
    {
        bytes = new byte[length];
        return base64 is not null
            && Convert.TryFromBase64String(base64, bytes, out int written)
            && written == length;
    }

    /// <summary>TLS 연결의 channel binding = Host 인증서(DER)의 SHA-256</summary>
    public static byte[] TlsChannelBinding(byte[] certificateDer) => SHA256.HashData(certificateDer);

    /// <summary>사람이 눈으로 비교하기 쉬운 형태: "AB12 CD34 ..." (SHA-256 64자리)</summary>
    public static string FormatFingerprint(ReadOnlySpan<byte> certificateHash)
    {
        string hex = Convert.ToHexString(certificateHash);
        return string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }
}
