using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testumgebung.Identity;

namespace Testumgebung.EntraBroker;

internal static class BrokerCredentials
{
    public static X509Certificate2 LoadCertificate(string? fingerprint)
    {
        if (!EntraBrokerProtocol.IsFingerprint(fingerprint))
            throw new InvalidOperationException("Ein SHA-256-Fingerabdruck für das Dienstzertifikat fehlt oder ist ungültig.");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificates = store.Certificates;
        try
        {
            var certificate = certificates.OfType<X509Certificate2>().SingleOrDefault(item =>
                item.GetCertHashString(HashAlgorithmName.SHA256).Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
            if (certificate is null || !certificate.HasPrivateKey ||
                DateTime.Now < certificate.NotBefore || DateTime.Now >= certificate.NotAfter)
                throw new InvalidOperationException("Das Dienstzertifikat fehlt, ist zeitlich ungültig oder hat keinen privaten Schlüssel.");
            return new X509Certificate2(certificate);
        }
        finally
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }

    public static string ReadSecret(string file)
    {
        var data = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.LocalMachine);
        try
        {
            var secret = Encoding.UTF8.GetString(data);
            return string.IsNullOrWhiteSpace(secret)
                ? throw new InvalidOperationException("Das hinterlegte Entra-Client-Secret ist leer.")
                : secret;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public static string CreateAssertion(X509Certificate2 certificate, string clientId, string tokenEndpoint)
    {
        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Das Entra-App-Zertifikat benötigt einen zugänglichen privaten RSA-Schlüssel.");
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = tokenEndpoint,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = clientId,
                ["jti"] = Guid.NewGuid().ToString()
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSsaPssSha256)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
            },
            AdditionalHeaderClaims = new Dictionary<string, object>
            {
                ["x5t#S256"] = Base64UrlEncoder.Encode(certificate.GetCertHash(HashAlgorithmName.SHA256))
            }
        });
    }
}
