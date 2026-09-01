using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Generates an ephemeral self-signed certificate for TLS tests (see TESTING.md). No checked-in
/// pfx: the key is created in memory and dies with the test process.
/// </summary>
public static class TlsTestCerts
{
    /// <summary>Creates a fresh self-signed server certificate valid for CN/SAN "test.local".</summary>
    public static X509Certificate2 Create(string cn = "test.local")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(cn);
        request.CertificateExtensions.Add(san.Build());

        var raw = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

        // Windows quirk: SChannel cannot use the CNG key embedded in a CertificateRequest-created
        // certificate ("Authentication failed because the platform does not support ephemeral keys").
        // Re-importing from PFX puts the key into user key storage, where SChannel can use it.
        // (Harmless on other platforms.) Note: library users who pass a raw CertificateRequest
        // certificate will have TLS fail on Windows for the same reason — see ARCHITECTURE.md §8.
        byte[] pfx = raw.Export(X509ContentType.Pkcs12, "test");
        raw.Dispose();

        return new X509Certificate2(pfx, "test", X509KeyStorageFlags.DefaultKeySet);
    }
}
