using FCG.Contracts;
using Xunit;

namespace IdentityContracts;

public class IdentityAuthorizationTests
{
    private static readonly string UserId = "22222222-2222-4222-8222-222222222222";
    private static TokenClaims Claims(string role = "User", string? subject = null) => new(subject ?? UserId, role, "55555555-5555-4555-8555-555555555555");

    [Theory]
    [InlineData(true, 401)]
    [InlineData(false, 503)]
    public void Users_distinguishes_real_absence_from_repository_unavailability(bool available, int expected)
    {
        Assert.Equal(expected, IdentityAuthorizationPolicy.Decide(true, Claims(), available, null, "UserOrAdmin", IdentityStateSource.UsersAccount).StatusCode);
    }

    [Theory]
    [InlineData(false, false, 401)]
    [InlineData(false, true, 401)]
    [InlineData(true, false, 503)]
    [InlineData(true, true, 503)]
    public void Invalid_token_precedes_missing_or_unavailable_state(bool valid, bool available, int expected)
    {
        var decision = IdentityAuthorizationPolicy.Decide(valid, Claims(), available, null, "UserOrAdmin");
        Assert.Equal(expected, decision.StatusCode);
        if (expected == 503) { Assert.Equal("auth_state_unavailable", decision.ErrorCode); Assert.Equal(1, decision.RetryAfterSeconds); }
    }

    [Theory]
    [InlineData("User", IdentityRole.Administrator, 403)]
    [InlineData("Administrator", IdentityRole.User, 403)]
    [InlineData("Administrator", IdentityRole.Administrator, 200)]
    public void Administration_requires_both_token_and_local_role(string role, IdentityRole localRole, int expected)
    {
        var current = IdentityWireTests.Snapshot() with { State = IdentityState.Active, Role = localRole };
        Assert.Equal(expected, IdentityAuthorizationPolicy.Decide(true, Claims(role), true, current, "AdminOnly").StatusCode);
    }

    [Theory]
    [InlineData(IdentityState.Blocked)]
    [InlineData(IdentityState.Deleted)]
    public void Known_inactive_account_is_unauthorized_before_role_check(IdentityState state)
    {
        var current = IdentityWireTests.Snapshot() with { State = state };
        Assert.Equal(401, IdentityAuthorizationPolicy.Decide(true, Claims(), true, current, "AdminOnly").StatusCode);
    }

    [Theory]
    [InlineData("Owner", "22222222-2222-4222-8222-222222222222")]
    [InlineData("", "22222222-2222-4222-8222-222222222222")]
    [InlineData("User", "invalid")]
    [InlineData("User", "00000000-0000-0000-0000-000000000000")]
    public void Missing_or_invalid_claims_do_not_become_infrastructure_error(string role, string sub)
    {
        Assert.Equal(401, IdentityAuthorizationPolicy.Decide(true, Claims(role, sub), false, null, "UserOrAdmin").StatusCode);
    }

    [Fact]
    public void Active_user_can_use_own_user_policy_but_not_another_subject_projection()
    {
        var current = IdentityWireTests.Snapshot() with { State = IdentityState.Active };
        Assert.Equal(200, IdentityAuthorizationPolicy.Decide(true, Claims(), true, current, "UserOrAdmin").StatusCode);
        Assert.Equal(401, IdentityAuthorizationPolicy.Decide(true, Claims(subject: "66666666-6666-4666-8666-666666666666"), true, current, "UserOrAdmin").StatusCode);
    }

    [Fact]
    public void Unknown_state_source_is_rejected_as_configuration_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityAuthorizationPolicy.Decide(true, Claims(), true, null, "UserOrAdmin", (IdentityStateSource)999));
    }

    [Fact]
    public void Missing_jti_is_invalid_even_when_state_is_unavailable()
    {
        Assert.Equal(401, IdentityAuthorizationPolicy.Decide(true, Claims() with { JwtId = "" }, false, null, "UserOrAdmin").StatusCode);
    }
}

public class JwtConfigurationTests
{
    private static JwtContractOptions Catalog() => new() { Issuer = "fcg-users", Audience = "fcg-api", PublicKeyPath = "/run/secrets/jwt-public.pem" };

    [Fact]
    public void Catalog_only_accepts_public_key_configuration()
    {
        Catalog().Validate(false);
        Assert.ThrowsAny<ArgumentException>(() => (Catalog() with { PrivateKeyPath = "/run/secrets/users-jwt-private.pem" }).Validate(false));
    }

    [Fact]
    public void Issuer_requires_explicit_persisted_private_key_path()
    {
        Assert.ThrowsAny<ArgumentException>(() => Catalog().Validate(true));
        (Catalog() with { PrivateKeyPath = "/run/secrets/users-jwt-private.pem" }).Validate(true);
    }

    [Theory]
    [InlineData("HS256", 15, 0)]
    [InlineData("RS256", 60, 0)]
    [InlineData("RS256", 15, 30)]
    public void Unsafe_algorithm_lifetime_or_skew_is_rejected(string algorithm, int minutes, int skew)
    {
        Assert.ThrowsAny<ArgumentException>(() => (Catalog() with { Algorithm = algorithm, ExpirationMinutes = minutes, ClockSkewSeconds = skew }).Validate(false));
    }

    [Fact]
    public void Missing_authority_or_public_key_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => (Catalog() with { Issuer = "" }).Validate(false));
        Assert.ThrowsAny<ArgumentException>(() => (Catalog() with { Audience = " " }).Validate(false));
        Assert.ThrowsAny<ArgumentException>(() => (Catalog() with { PublicKeyPath = "" }).Validate(false));
    }
}
