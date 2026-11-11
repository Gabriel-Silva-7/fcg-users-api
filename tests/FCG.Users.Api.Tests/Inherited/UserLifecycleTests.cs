using FCG.Users.Domain.Identity;
using Xunit;

namespace FCG.Users.Api.Tests.Inherited;

public sealed class UserLifecycleTests
{
    [Fact]
    public void Effective_status_changes_increment_state_version_but_noops_do_not()
    {
        var user = New(); user.ChangeActiveStatus(true); Assert.Equal(1, user.StateVersion);
        user.ChangeActiveStatus(false); Assert.Equal(2, user.StateVersion); user.ChangeActiveStatus(false); Assert.Equal(2, user.StateVersion);
        user.ChangeActiveStatus(true); Assert.Equal(3, user.StateVersion);
    }
    [Fact]
    public void Logical_deletion_retains_identity_and_credentials_and_cannot_be_reversed()
    {
        var user = New(); var identity = (user.Id, user.Name, user.Email, user.PasswordHash, user.Role, user.CreatedAtUtc);
        Assert.True(user.SoftDelete()); Assert.False(user.IsActive); Assert.True(user.IsDeleted); Assert.Equal(2, user.StateVersion);
        Assert.False(user.SoftDelete()); Assert.Equal(2, user.StateVersion);
        Assert.Throws<InvalidOperationException>(() => user.ChangeActiveStatus(true));
        Assert.Throws<InvalidOperationException>(() => user.ChangeActiveStatus(false));
        Assert.Equal(identity, (user.Id, user.Name, user.Email, user.PasswordHash, user.Role, user.CreatedAtUtc));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Version_overflow_does_not_partially_change_state(bool delete)
    {
        var user = New(); typeof(User).GetProperty(nameof(User.StateVersion))!.SetValue(user, long.MaxValue);
        Assert.Throws<OverflowException>(() => { if (delete) user.SoftDelete(); else user.ChangeActiveStatus(false); });
        Assert.True(user.IsActive); Assert.False(user.IsDeleted); Assert.Equal(long.MaxValue, user.StateVersion);
    }
    private static User New() => User.Register("Person", Email.Create("person@example.com"), "private-hash", DateTime.UtcNow);
}
