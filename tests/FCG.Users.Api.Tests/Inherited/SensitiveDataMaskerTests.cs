using Xunit;
using FCG.Users.Application.Common;

namespace FCG.Users.Application.Tests.Common;

public sealed class SensitiveDataMaskerTests
{
    [Theory]
    [InlineData("gabriel@example.com", "g***@example.com")]
    [InlineData("biel14022002@gmail.com", "b***@gmail.com")]
    [InlineData("ab@x.io", "a***@x.io")]
    public void MaskEmail_KeepsOnlyTheFirstCharacterOfTheLocalPart(string email, string expected)
    {
        Assert.Equal(expected, SensitiveDataMasker.MaskEmail(email));
    }

    [Fact]
    public void MaskEmail_HidesASingleCharacterLocalPartEntirely()
    {
        Assert.Equal("***@gmail.com", SensitiveDataMasker.MaskEmail("b@gmail.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sem-arroba")]
    [InlineData("@example.com")]
    [InlineData("gabriel@")]
    [InlineData("a@b@c.com")]
    // Injeção de log: o e-mail chega cru do cliente, e uma quebra de linha ecoada forjaria uma
    // entrada inteira no log. Nada que o domínio não reconheça como e-mail pode ser devolvido.
    [InlineData("a@example.com\n2026-09-01 warn: UserBlocked 111 222 forjado")]
    [InlineData("a@example.com\rUserRegistered")]
    [InlineData("a@example.com com espaço")]
    public void MaskEmail_NeverEchoesInputThatIsNotAnEmail(string? value)
    {
        Assert.Equal("***", SensitiveDataMasker.MaskEmail(value));
    }
}
