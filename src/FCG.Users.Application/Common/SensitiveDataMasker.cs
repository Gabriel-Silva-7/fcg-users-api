using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Common;

public static class SensitiveDataMasker
{
    public const string FullyMasked = "***";

    // O e-mail chega cru do cliente em LoginFailed: a rota aceita qualquer string para não
    // permitir enumeração de contas. Ecoar o que vem depois do '@' deixaria um anônimo escrever
    // no log — inclusive quebras de linha, forjando uma entrada inteira. Só um e-mail que o
    // domínio reconhece é mascarado; qualquer outra coisa vira ***.
    public static string MaskEmail(string? email)
    {
        if (!Email.TryCreate(email, out var parsed))
        {
            return FullyMasked;
        }

        var value = parsed.Value;
        var separatorIndex = value.IndexOf('@', StringComparison.Ordinal);
        var domain = value[separatorIndex..];

        return separatorIndex == 1
            ? FullyMasked + domain
            : value[0] + FullyMasked + domain;
    }
}
