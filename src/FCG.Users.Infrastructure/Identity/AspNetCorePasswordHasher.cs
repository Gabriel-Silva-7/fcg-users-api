using FCG.Users.Application.Identity;
using Microsoft.AspNetCore.Identity;

namespace FCG.Users.Infrastructure.Identity;

public sealed class AspNetCorePasswordHasher : IPasswordHasher
{
    private static readonly object UserContext = new();

    private readonly PasswordHasher<object> _passwordHasher = new();
    private readonly string _placeholderHash;

    public AspNetCorePasswordHasher()
    {
        _placeholderHash = _passwordHasher.HashPassword(UserContext, Guid.NewGuid().ToString());
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        return _passwordHasher.HashPassword(UserContext, password);
    }

    public bool Verify(string? passwordHash, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        // Conta inexistente também percorre o PBKDF2 para reduzir a diferença observável no login.
        var hashToVerify = passwordHash ?? _placeholderHash;
        var verified = _passwordHasher.VerifyHashedPassword(UserContext, hashToVerify, password) is not
            PasswordVerificationResult.Failed;

        return passwordHash is not null && verified;
    }
}
