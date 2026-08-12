using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Glyph3.Playground.MsQuic;

/// <summary>
/// A self-signed localhost certificate, so the sample runs with no setup.
///
/// QUIC is TLS 1.3 all the way down - there is no cleartext HTTP/3 - so a certificate is not
/// optional the way it is over TCP. Rather than make that the first thing anyone hits, one is
/// generated in memory per run. It is enough for <c>curl --http3 -k</c>; for anything real, load
/// your own and hand it to <c>SslServerAuthenticationOptions.ServerCertificate</c>.
/// </summary>
internal static class DevCertificate
{
    public static X509Certificate2 CreateSelfSigned(string hostname)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Without a SAN this certificate cannot be VERIFIED by anything modern, only skipped with
        // -k: clients stopped reading CN for identity years ago (RFC 6125).
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostname);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // SslStream and MsQuic on Linux want the private key associated through a PFX round-trip;
        // a certificate built from a CertificateRequest carries it in a form they will not use.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}
