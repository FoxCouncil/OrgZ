// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// The share's self-signed TLS identity. Created once and persisted beside the library
/// database, so the certificate - and therefore the pin a client remembers - stays
/// stable across restarts. This is deliberately privacy-grade, not PKI-grade: the pin
/// rides the mDNS announcement, which an ACTIVE on-link attacker could rewrite along
/// with everything else. What it ends: passive listeners reading titles and audio off
/// the wire, and lazy proxy-in-the-middle boxes that count on plaintext.
/// </summary>
public static class ShareCertificate
{
    private static readonly ILogger _log = Logging.For("ShareCertificate");

    internal const string FileName = "share-cert.pfx";

    /// <summary>
    /// Loads the persisted certificate from <paramref name="directory"/>, creating and
    /// persisting a fresh one when absent or unreadable. Never throws: a certificate
    /// that can't be persisted is still returned ephemeral (the pin just changes next
    /// run, which a client re-learns from the announcement).
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string directory)
    {
        var path = Path.Combine(directory, FileName);

        try
        {
            if (File.Exists(path))
            {
                return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.Exportable);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Persisted share certificate unreadable - regenerating");
        }

        var cert = Create();

        try
        {
            Directory.CreateDirectory(directory);

            // The PKCS#12 carries the private key with no password on it, so it must never
            // exist - not even for the length of a write - at the umask default of 0644. The
            // helper daemon writes this as root into the user's OWN directory, and anyone who
            // can read the file can present this identity to every client holding the pin.
            var pkcs12 = cert.Export(X509ContentType.Pkcs12);
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                file.Write(pkcs12, 0, pkcs12.Length);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not persist the share certificate - the pin will rotate on restart");
        }

        return cert;
    }

    /// <summary>
    /// RSA rather than ECDSA purely for TLS-stack compatibility breadth across the three
    /// platforms' Schannel / SecureTransport / OpenSSL. Ten years: rotation is not a
    /// goal for a LAN privacy cert whose trust anchor is the announcement pin.
    /// </summary>
    internal static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=OrgZ Library Share", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));   // serverAuth

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        // Round-trip through PKCS12: Schannel refuses to serve from a cert whose private
        // key only exists as the in-memory CNG object CreateSelfSigned leaves behind.
        var portable = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
        cert.Dispose();
        return portable;
    }

    /// <summary>
    /// The pin a client matches: base64 of SHA-256 over the certificate's DER bytes.
    /// Cert-hash rather than SPKI-hash because the certificate itself is persisted -
    /// there is no re-issuing under a stable key to accommodate.
    /// </summary>
    public static string PinOf(X509Certificate2 certificate)
        => Convert.ToBase64String(SHA256.HashData(certificate.RawData));
}
