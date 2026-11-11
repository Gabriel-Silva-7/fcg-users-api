using Xunit;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Domain.Tests.Identity;

public sealed class EmailTests
{
    [Fact]
    public void Create_WhenValueIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Email.Create(null!));
    }

    [Fact]
    public void Create_WhenValueExceeds256Characters_ThrowsArgumentException()
    {
        var localPart = new string('a', 64);
        var domain = string.Join(
            '.',
            new string('b', 48),
            new string('c', 48),
            new string('d', 48),
            new string('e', 45));
        var value = $"{localPart}@{domain}";

        Assert.Equal(257, value.Length);
        Assert.Throws<ArgumentException>(() => Email.Create(value));
    }

    [Fact]
    public void Create_NormalizesWhitespaceAndCasing()
    {
        var email = Email.Create("  Gabriel@Example.COM  ");

        Assert.Equal("gabriel@example.com", email.Value);
    }

    [Fact]
    public void Emails_WithSameNormalizedValue_AreEqual()
    {
        var first = Email.Create("User@Example.com");
        var second = Email.Create("user@example.com");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ToString_ReturnsNormalizedValue()
    {
        var email = Email.Create("User@Example.com");

        Assert.Equal("user@example.com", email.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-email")]
    [InlineData("missing-domain@")]
    [InlineData("@missing-local-part.com")]
    public void Create_WhenFormatIsInvalid_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() => Email.Create(value));
    }

    [Theory]
    [InlineData("first.last@example.com")]
    [InlineData("user+tag@example.com")]
    [InlineData("user@sub.example.com")]
    [InlineData("user@example.co.uk")]
    [InlineData("user@prefeitura.eti.br")]
    [InlineData("user@example.bio")]
    [InlineData("user@example.us")]
    [InlineData("user@example.cc")]
    [InlineData("user@example.io")]
    public void Create_WhenAddressIsValid_AcceptsEmail(string value)
    {
        var email = Email.Create(value);

        Assert.Equal(value, email.Value);
    }

    [Fact]
    public void TryCreate_WhenValueIsValid_ReturnsTrueAndNormalizedEmail()
    {
        var succeeded = Email.TryCreate("  User@Example.COM  ", out var email);

        Assert.True(succeeded);
        Assert.NotNull(email);
        Assert.Equal("user@example.com", email.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-email")]
    public void TryCreate_WhenValueIsInvalid_ReturnsFalseWithoutThrowing(string? value)
    {
        var succeeded = Email.TryCreate(value, out var email);

        Assert.False(succeeded);
        Assert.Null(email);
    }
}
