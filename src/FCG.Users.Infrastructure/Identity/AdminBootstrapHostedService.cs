using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FCG.Users.Infrastructure.Identity;

public sealed class AdminBootstrapStartupException() : Exception("User bootstrap failed; inspect the safe startup diagnostic and configuration.");

public sealed class AdminBootstrapHostedService(IOptions<AdminBootstrapOptions> options, IServiceScopeFactory scopes,
    ILogger<AdminBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<AdminBootstrapService>().ExecuteAsync(cancellationToken);
            logger.LogInformation("AdminBootstrapCompleted {Result}", outcome.Administrator);
            logger.LogInformation("PlayerSeedCompleted {Result}", outcome.Player);
            foreach (var account in outcome.CreatedAccounts)
                logger.LogInformation("UserRegistered {TargetUserId} {MaskedEmail} {TraceId}", account.UserId, account.MaskedEmail, (string?)null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Hosting may log the exception again. Never give it the original message/inner exception containing secrets.
            logger.LogError("AdminBootstrapFailed {FailureType}", error.GetType().Name);
            throw new AdminBootstrapStartupException();
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
