namespace FCG.Contracts;

public enum IdentityStateSource { UsersAccount, CatalogProjection }

public sealed record TokenClaims(string Subject, string Role, string JwtId);
public sealed record AuthorizationDecision(int StatusCode, string? ErrorCode = null, int? RetryAfterSeconds = null);

/// <summary>Policy contract after cryptographic validation; does not authenticate HTTP requests.</summary>
public static class IdentityAuthorizationPolicy
{
    public const string UserOrAdmin = "UserOrAdmin";
    public const string AdminOnly = "AdminOnly";

    public static AuthorizationDecision Decide(bool tokenCryptographicallyValid, TokenClaims? token,
        bool stateAvailable, IdentitySnapshot? current, string policy, IdentityStateSource source = IdentityStateSource.CatalogProjection)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if (!tokenCryptographicallyValid || token is null || !Guid.TryParse(token.Subject, out var userId) || userId == Guid.Empty
            || !Guid.TryParse(token.JwtId, out var tokenId) || tokenId == Guid.Empty
            || (token.Role != "User" && token.Role != "Administrator")) return new(401);
        if (!stateAvailable) return new(503, "auth_state_unavailable", 1);
        if (current is null) return source == IdentityStateSource.UsersAccount
            ? new(401) : new(503, "auth_state_unavailable", 1);
        IdentitySnapshotValidation.Validate(current);
        if (current.UserId != userId || current.State != IdentityState.Active) return new(401);
        return policy switch
        {
            UserOrAdmin => new(200),
            AdminOnly when token.Role == "Administrator" && current.Role == IdentityRole.Administrator => new(200),
            _ => new(403)
        };
    }
}
