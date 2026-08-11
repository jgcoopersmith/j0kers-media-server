using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Services;

/// <summary>
/// The certificate the HTTPS listeners present.
///
/// Either the administrator's own — a .pfx from a real CA, or from their
/// own — or one this server makes for itself. A self-signed certificate is
/// the honest default for a box on a home network that has no public name
/// to be certified for: it encrypts the connection, which is the point, but
/// a browser will warn the first time because nothing vouches for it.
/// Import it once on each device, or point <c>https.certificate</c> at a
/// proper one.
///
/// The generated certificate names every way the server can be reached —
/// hostname, hostname.local, localhost, and each active address — so it
/// stops being wrong the moment someone connects by IP instead of by name.
/// </summary>
public static class TlsCertificate
{
    private const string FileName = "server.pfx";

    /// <summary><paramref name="Password"/> is what the PFX on disk needs, for the store import.</summary>
    public sealed record Loaded(X509Certificate2 Certificate, string Path, bool SelfSigned, string Password = "");

    /// <summary>
    /// Returns the certificate to bind, generating and saving one if the
    /// config names none. Null when it cannot be had at all, which leaves
    /// the caller to fall back to plain HTTP rather than not start.
    /// </summary>
    public static Loaded? Ensure(Config.HttpsConfig https, string baseDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(https.Certificate))
            {
                var path = System.IO.Path.IsPathRooted(https.Certificate)
                    ? https.Certificate
                    : System.IO.Path.Combine(baseDirectory, https.Certificate);
                if (!File.Exists(path))
                {
                    Log.Error("tls", $"certificate not found: {path}");
                    return null;
                }
                var supplied = X509CertificateLoader.LoadPkcs12FromFile(
                    path, https.Password.Length > 0 ? https.Password : null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                if (!supplied.HasPrivateKey)
                {
                    Log.Error("tls", $"{System.IO.Path.GetFileName(path)} has no private key — TLS needs one");
                    return null;
                }
                Log.Info("tls", $"using {System.IO.Path.GetFileName(path)} " +
                                $"(subject {supplied.Subject}, expires {supplied.NotAfter:yyyy-MM-dd})");
                return new Loaded(supplied, path, false, https.Password);
            }

            var own = System.IO.Path.Combine(baseDirectory, FileName);
            if (File.Exists(own))
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(
                    own, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                // a certificate that has expired is worse than none: browsers
                // refuse it outright rather than warning, so make a new one
                if (existing.NotAfter > DateTime.Now.AddDays(7))
                {
                    SecretFile.Protect(own);
                    Log.Info("tls", $"using the server's own certificate (expires {existing.NotAfter:yyyy-MM-dd})");
                    return new Loaded(existing, own, true);
                }
                Log.Info("tls", "the server's certificate has expired — generating a new one");
            }

            var made = Generate();
            File.WriteAllBytes(own, made.Export(X509ContentType.Pfx));
            SecretFile.Protect(own);
            Log.Info("tls", $"generated a self-signed certificate for {made.Subject} " +
                            $"(expires {made.NotAfter:yyyy-MM-dd}) — browsers will warn until it is trusted");
            return new Loaded(made, own, true);
        }
        catch (Exception ex)
        {
            Log.Error("tls", $"could not prepare a certificate: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A self-signed certificate for this machine, under every name and
    /// address it answers to.
    /// </summary>
    private static X509Certificate2 Generate()
    {
        var host = Dns.GetHostName();
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName($"{host}.local");
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var i in NetworkInfo.Active())
            if (IPAddress.TryParse(i.Address, out var ip)) san.AddIpAddress(ip);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}, O=j0kers Media Server",
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false)); // server auth
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        // Two years: long enough not to be a chore, short enough that a key
        // living in a file on a media server does not outlive its welcome.
        var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));

        // Round-tripping through PFX is what gives the key a persisted home
        // on Windows; without it the private key vanishes with the process.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
}
