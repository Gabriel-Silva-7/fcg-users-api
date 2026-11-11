using FCG.Users.Domain.Identity;
using Xunit;

namespace FCG.Users.Api.Tests.Inherited;

public sealed class UserPasswordStateTests
{
    [Fact]
    public void Password_change_preserves_shared_identity_snapshot_and_logical_version()
    {
        var user = New(); var snapshot = (user.Id, user.Name, user.Email, user.Role, user.IsActive, user.IsDeleted, user.StateVersion);
        user.ChangePasswordHash("new-private-hash"); Assert.Equal("new-private-hash", user.PasswordHash);
        Assert.Equal(snapshot, (user.Id, user.Name, user.Email, user.Role, user.IsActive, user.IsDeleted, user.StateVersion));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Deleted_or_blocked_account_cannot_change_password(bool deleted)
    {
        var user = New(); if (deleted) user.SoftDelete(); else user.ChangeActiveStatus(false);
        Assert.Throws<InvalidOperationException>(() => user.ChangePasswordHash("attempted-hash"));
        Assert.Equal("old-private-hash", user.PasswordHash); Assert.Equal(2, user.StateVersion);
    }
    private static User New() => User.Register("Person", Email.Create("person@example.com"), "old-private-hash", DateTime.UtcNow);
}
