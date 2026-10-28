using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
namespace FCG.Users.Api;
public sealed class UsersDatabaseHealthCheck(UsersDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Query all mapped columns so a partially applied schema is not reported ready.
            await db.Users.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        { return HealthCheckResult.Unhealthy("Users storage is unavailable."); }
    }
}
