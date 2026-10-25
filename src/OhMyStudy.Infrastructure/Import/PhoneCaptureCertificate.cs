using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OhMyStudy.Infrastructure.Import;

public sealed class PhoneCaptureCertificate : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public X509Certificate2Collection Chain { get; }

    public PhoneCaptureCertificate(IPAddress address)
    {
        if (!IPAddress.IsLoopback(address) && (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            address.GetAddressBytes() is not [10, _, _, _] and not [172, >= 16 and <= 31, _, _] and not [192, 168, _, _]))
            throw new ArgumentException("Phone capture certificates require a loopback or private LAN address", nameof(address));
        using var stream = typeof(PhoneCaptureCertificate).Assembly.GetManifestResourceStream("OhMyStudy.PhoneCapture.Issuer.pem")!;
        using var reader = new StreamReader(stream);
        var issuer = X509Certificate2.CreateFromPem(reader.ReadToEnd());
        if (!issuer.GetCertHashString(HashAlgorithmName.SHA256).Equals("7485B51C0ED192878FA1D2F948FB529AE1842CC5086C4B67F7BE5B2039DE9489", StringComparison.Ordinal))
        {
            issuer.Dispose();
            throw new CryptographicException("Unexpected phone capture experimental issuer certificate");
        }
        Chain = [issuer];
        using var publicKey = issuer.GetECDsaPublicKey()!;
        var parameters = publicKey.ExportParameters(false);
        parameters.D = new byte[48];
        parameters.D[^1] = 4;
        using var signingKey = ECDsa.Create(parameters);
        using var signer = issuer.CopyWithPrivateKey(signingKey);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName("CN=Oh My Study LAN Capture"), leafKey, HashAlgorithmName.SHA384);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        var now = DateTimeOffset.UtcNow;
        using var leaf = request.Create(signer, now.AddMinutes(-5), now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var keyed = leaf.CopyWithPrivateKey(leafKey);
        byte[] pkcs12 = keyed.Export(X509ContentType.Pkcs12);
        try { Certificate = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pkcs12); }
    }

    public void Dispose()
    {
        Certificate.Dispose();
        foreach (var certificate in Chain) certificate.Dispose();
    }
}
