using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Core;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// LAN TLS용 Host 인증서.
/// Windows 인증서 저장소(현재 사용자\개인)에 저장합니다. 개인 키는 Windows가 사용자 계정으로 보호합니다.
/// </summary>
public static class HostCertificate
{
    public static X509Certificate2 LoadOrCreate(string hostId)
    {
        string subject = $"CN=RemoteDesktop Host {hostId}";

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        X509Certificate2? existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, subject, validOnly: false)
            .Where(certificate => certificate.HasPrivateKey && certificate.NotAfter > DateTime.Now.AddDays(30))
            .OrderByDescending(certificate => certificate.NotAfter)
            .FirstOrDefault();

        if (existing is not null)
        {
            return existing;
        }

        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        using X509Certificate2 created = request.CreateSelfSigned(
            DateTimeOffset.Now.AddDays(-1),
            DateTimeOffset.Now.AddYears(2));

        // Windows의 SslStream은 저장된(persisted) 키가 필요하므로 PFX로 내보낸 뒤 다시 불러옵니다.
        var persisted = new X509Certificate2(
            created.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);

        store.Add(persisted);
        Log.Info("새 TLS 인증서를 만들어 Windows 인증서 저장소(현재 사용자\\개인)에 저장했습니다.");
        return persisted;
    }
}
