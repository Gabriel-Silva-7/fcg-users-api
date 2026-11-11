using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FCG.Contracts;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
namespace FCG.Users.Infrastructure.Identity;
public sealed class JwtTokenGenerator(IOptions<JwtContractOptions> options, JwtKeyMaterial keys, TimeProvider clock) : IJwtTokenGenerator
{
    public AccessToken Generate(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var jwt = options.Value;
        var issued = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds()).UtcDateTime;
        var claims = new[] { new Claim("sub", user.Id.ToString()), new Claim("role", user.Role.ToString()), new Claim("jti", Guid.NewGuid().ToString()) };
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience, claims, issued, issued.AddMinutes(jwt.ExpirationMinutes),
            new SigningCredentials(keys.SigningKey, SecurityAlgorithms.RsaSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), checked(jwt.ExpirationMinutes * 60));
    }
}
