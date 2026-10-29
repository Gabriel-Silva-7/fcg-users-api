using FCG.Users.Domain.Identity;
using Microsoft.Extensions.Options;

namespace FCG.Users.Infrastructure.Identity;

public sealed class AdminBootstrapOptionsValidator : IValidateOptions<AdminBootstrapOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminBootstrapOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        List<string> errors = [];
        ValidateAccount(options.Email, options.Password, "administrator", errors);
        ValidateAccount(options.PlayerEmail, options.PlayerPassword, "player", errors);
        if (Email.TryCreate(options.Email, out var admin) && Email.TryCreate(options.PlayerEmail, out var player) && admin == player)
            errors.Add("AdminBootstrap requires distinct administrator and player addresses.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
    private static void ValidateAccount(string? email, string? password, string account, List<string> errors)
    {
        if (!Email.TryCreate(email, out _)) errors.Add($"AdminBootstrap requires a valid {account} email.");
        try { PasswordPolicy.EnsureIsValid(password!); }
        catch (ArgumentException) { errors.Add($"AdminBootstrap requires a {account} password satisfying the password policy."); }
    }
}
