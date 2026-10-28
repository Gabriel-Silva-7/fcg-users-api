using Xunit;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Domain.Tests.Identity;

public sealed class PasswordPolicyTests
{
    [Fact]
    public void EnsureIsValid_WhenPasswordIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => PasswordPolicy.EnsureIsValid(null!));

        Assert.Equal("password", exception.ParamName);
    }

    [Fact]
    public void EnsureIsValid_WhenPasswordHasNoDigit_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PasswordPolicy.EnsureIsValid("Abcdefg!"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains("at least one digit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureIsValid_WhenPasswordHasNoLetter_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PasswordPolicy.EnsureIsValid("1234567!"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains("at least one letter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureIsValid_WhenPasswordHasFewerThanEightCharacters_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PasswordPolicy.EnsureIsValid("Ab1!"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains("at least 8 characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureIsValid_WhenPasswordHasNoSpecialCharacter_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PasswordPolicy.EnsureIsValid("Abcdefg1"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains("at least one special character", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureIsValid_WhenPasswordUsesWhitespaceAsSpecialCharacter_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PasswordPolicy.EnsureIsValid("Abcdef1 "));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains("at least one special character", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureIsValid_WhenPasswordExceedsMaximumLength_ThrowsArgumentException()
    {
        var password = "Ab1!" + new string('a', PasswordPolicy.MaximumLength);

        var exception = Assert.Throws<ArgumentException>(
            () => PasswordPolicy.EnsureIsValid(password));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains("cannot exceed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureIsValid_WhenPasswordHasMaximumLength_DoesNotThrow()
    {
        var password = "Ab1!" + new string('a', PasswordPolicy.MaximumLength - 4);

        var exception = Record.Exception(
            () => PasswordPolicy.EnsureIsValid(password));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureIsValid_WhenPasswordMeetsEveryRequirement_DoesNotThrow()
    {
        var exception = Record.Exception(
            () => PasswordPolicy.EnsureIsValid("Abcdef1!"));

        Assert.Null(exception);
    }
}
