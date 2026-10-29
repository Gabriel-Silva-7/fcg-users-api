using FCG.Users.Api.Operations;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

public sealed class IdentityReconciliationOptionsTests
{
    public static IEnumerable<object[]> Invalid => new[] { Array.Empty<string>(), new[] { "--dry-run" }, new[] { "--all", "--all" }, new[] { "--all", "--user-id", Guid.NewGuid().ToString() }, new[] { "--user-id" }, new[] { "--user-id", "invalid" }, new[] { "--user-id", Guid.Empty.ToString() }, new[] { "--all", "--dry-run", "--dry-run" }, new[] { "--ALL" }, new[] { "--all", "--unsupported" } }.Select(args => new object[] { args });
    [Theory]
    [MemberData(nameof(Invalid))]
    public void Selection_is_explicit_and_unknown_or_conflicting_arguments_are_rejected(string[] args)
    { Assert.False(IdentityReconciliationOptions.TryParse(args, out var result)); Assert.Null(result); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void All_selection_accepts_optional_dry_run(bool dry)
    { Assert.True(IdentityReconciliationOptions.TryParse(dry ? ["--dry-run", "--all"] : ["--all"], out var result)); Assert.Null(result!.UserId); Assert.Equal(dry, result.DryRun); }
    [Fact]
    public void Individual_selection_accepts_a_nonempty_identity()
    { var id = Guid.NewGuid(); Assert.True(IdentityReconciliationOptions.TryParse(["--user-id", id.ToString(), "--dry-run"], out var result)); Assert.Equal(id, result!.UserId); Assert.True(result.DryRun); }
}
