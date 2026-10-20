namespace FCG.Contracts;

/// <summary>Configuration contract only; not registered in the host in this increment.</summary>
public sealed record JwtContractOptions
{
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    public string Algorithm { get; init; } = "RS256";
    public int ExpirationMinutes { get; init; } = 15;
    public int ClockSkewSeconds { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string PublicKeyPath { get; init; } = "";

    public void Validate(bool isIssuer)
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience) || string.IsNullOrWhiteSpace(PublicKeyPath))
            throw new ArgumentException("JWT issuer, audience and public key path are required.");
        if (Algorithm != "RS256" || ExpirationMinutes != 15 || ClockSkewSeconds != 0)
            throw new ArgumentException("JWT contract requires RS256, a 15-minute lifetime and zero clock skew.");
        if (isIssuer && string.IsNullOrWhiteSpace(PrivateKeyPath))
            throw new ArgumentException("Users must configure its persisted private key path.");
        if (!isIssuer && PrivateKeyPath is not null)
            throw new ArgumentException("A validator must not receive a private key configuration.");
        if (isIssuer && string.Equals(PrivateKeyPath, PublicKeyPath, StringComparison.Ordinal))
            throw new ArgumentException("Private and public key files must be separate.");
    }
}
