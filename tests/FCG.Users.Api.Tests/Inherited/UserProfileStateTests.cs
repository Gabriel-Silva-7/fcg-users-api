using FCG.Users.Domain.Identity;
using Xunit;

namespace FCG.Users.Api.Tests.Inherited;

public sealed class UserProfileStateTests
{
    [Fact]
    public void Profile_snapshot_version_advances_once_for_real_change_and_not_for_normalized_noop()
    {
        var user = New(); user.UpdateProfile("  Original  ", Email.Create("ORIGINAL@EXAMPLE.COM")); Assert.Equal(1, user.StateVersion);
        user.UpdateProfile("Updated", Email.Create("updated@example.com")); Assert.Equal(2, user.StateVersion);
        user.UpdateProfile(" Updated ", Email.Create(" UPDATED@EXAMPLE.COM ")); Assert.Equal(2, user.StateVersion);
        Assert.Equal("private-hash", user.PasswordHash); Assert.Equal(UserRole.User, user.Role);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Inactive_or_deleted_account_cannot_edit_profile(bool deleted)
    {
        var user = New(); if (deleted) user.SoftDelete(); else user.ChangeActiveStatus(false);
        Assert.Throws<InvalidOperationException>(() => user.UpdateProfile("Attempt", Email.Create("attempt@example.com")));
        Assert.Equal("Original", user.Name); Assert.Equal("original@example.com", user.Email.Value); Assert.Equal(2, user.StateVersion);
    }
    [Fact]
    public void Overflow_does_not_partially_change_contact()
    {
        var user = New(); typeof(User).GetProperty(nameof(User.StateVersion))!.SetValue(user, long.MaxValue);
        Assert.Throws<OverflowException>(() => user.UpdateProfile("Attempt", Email.Create("attempt@example.com")));
        Assert.Equal("Original", user.Name); Assert.Equal("original@example.com", user.Email.Value);
    }
    private static User New() => User.Register("Original", Email.Create("original@example.com"), "private-hash", DateTime.UtcNow);
}
