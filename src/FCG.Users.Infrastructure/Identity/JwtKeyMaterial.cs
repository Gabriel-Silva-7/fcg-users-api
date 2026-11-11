using System.Security.Cryptography;
using FCG.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
namespace FCG.Users.Infrastructure.Identity;

/// <summary>Loads a persisted PKCS#8/SPKI RSA pair; never generates or logs credentials.</summary>
public sealed class JwtKeyMaterial : IDisposable
{
    private readonly RSA _privateKey = RSA.Create();
    private readonly RSA _publicKey = RSA.Create();
    public RsaSecurityKey SigningKey { get; }
    public RsaSecurityKey ValidationKey { get; }
    public JwtKeyMaterial(IOptions<JwtContractOptions> options)
    {
        try
        {
            var jwt = options.Value;
            jwt.Validate(true);
            var privateBytes = ReadPem(jwt.PrivateKeyPath!, "PRIVATE KEY");
            try
            {
                _privateKey.ImportPkcs8PrivateKey(privateBytes, out var read);
                if (read != privateBytes.Length) throw new CryptographicException();
            }
            finally { CryptographicOperations.ZeroMemory(privateBytes); }
            var publicBytes = ReadPem(jwt.PublicKeyPath, "PUBLIC KEY");
            _publicKey.ImportSubjectPublicKeyInfo(publicBytes, out var publicRead);
            if (publicRead != publicBytes.Length || _privateKey.KeySize < 2048 || _publicKey.KeySize < 2048)
                throw new CryptographicException();
            if (!_privateKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(_publicKey.ExportSubjectPublicKeyInfo()))
                throw new CryptographicException();
            // Providers otherwise share a process-wide cache keyed by RSA material. A provider
            // from another host may retain that host's RSA after it has been disposed.
            var factory = new CryptoProviderFactory { CacheSignatureProviders = false };
            SigningKey = new RsaSecurityKey(_privateKey) { CryptoProviderFactory = factory };
            ValidationKey = new RsaSecurityKey(_publicKey) { CryptoProviderFactory = factory };
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            Dispose();
            throw new InvalidOperationException("JWT requires a matching persisted RSA key pair (PKCS#8 private, SPKI public, at least 2048 bits).");
        }
    }
    private static byte[] ReadPem(string path, string expectedLabel)
    {
        var pem = File.ReadAllText(path).Trim();
        var fields = PemEncoding.Find(pem);
        if (!pem[fields.Label].Equals(expectedLabel, StringComparison.Ordinal) || pem[fields.Location] != pem)
            throw new CryptographicException();
        return Convert.FromBase64String(pem[fields.Base64Data]);
    }
    public void Dispose() { _privateKey.Dispose(); _publicKey.Dispose(); }
}
