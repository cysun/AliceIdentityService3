using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AliceIdentityService.Services;

// Persistent passwordless certificates, using the same filenames as AIS2.
public sealed class DeploymentCertificates : IDisposable
{
    private readonly List<X509Certificate2> loaded = [];
    public X509Certificate2 Signing { get; }
    public X509Certificate2 Encryption { get; }

    public DeploymentCertificates(IConfiguration configuration)
    {
        var folder = configuration["Application:CertificateFolder"];
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new InvalidOperationException("Application:CertificateFolder must be an absolute persistent directory.");
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
            else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Signing = LoadOrCreate(folder, "signing-certificate.pfx", X509KeyUsageFlags.DigitalSignature);
            Encryption = LoadOrCreate(folder, "encryption-certificate.pfx", X509KeyUsageFlags.KeyEncipherment);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dispose();
            throw new InvalidOperationException("Application:CertificateFolder must be readable and writable by the AIS3 service account.");
        }
        catch { Dispose(); throw; }
    }

    private X509Certificate2 LoadOrCreate(string folder, string filename, X509KeyUsageFlags usage)
    {
        try
        {
            var path = Path.Combine(folder, filename);
            if (!File.Exists(path)) CreateFile(path, usage);
            // macOS uses temporary keychain handles, disposed with the certificate.
            var storage = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, storage);
            loaded.Add(certificate);
            using var rsa = certificate.GetRSAPrivateKey();
            var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            if (rsa is null || rsa.KeySize < 2048 || (keyUsage is not null && !keyUsage.KeyUsages.HasFlag(usage)) ||
                certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new InvalidOperationException();
            return certificate;
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Never replace an existing invalid/unreadable file or expose loader exception details.
            throw new InvalidOperationException($"Could not load or create {filename} in Application:CertificateFolder. Existing files must be passwordless PKCS#12 certificates with a currently valid RSA private key of at least 2048 bits and the appropriate key usage; existing files are never overwritten.");
        }
    }

    private static void CreateFile(string path, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(3072);
        var purpose = usage == X509KeyUsageFlags.DigitalSignature ? "Signing" : "Encryption";
        var request = new CertificateRequest($"CN=AIS3 {purpose} Certificate", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        // X.509 requires NotAfter. Year 9999 avoids routine expiry/renewal for these application keys.
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero));
        var bytes = certificate.Export(X509ContentType.Pfx, string.Empty);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporaryPath, options))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            // Publish only a complete file. If another startup wins the race, load its certificate.
            try { File.Move(temporaryPath, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            File.Delete(temporaryPath);
        }
    }

    public void Dispose() { foreach (var certificate in loaded) certificate.Dispose(); }
}
