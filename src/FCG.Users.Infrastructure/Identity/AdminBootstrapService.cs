using FCG.Users.Application.Common;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Persistence;
using FCG.Users.Infrastructure.Persistence.Configurations.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FCG.Users.Infrastructure.Identity;

public enum AdminBootstrapResult { Disabled, Created, AlreadyConfigured }
public sealed record BootstrapCreatedAccount(Guid UserId, string MaskedEmail);
public sealed record AdminBootstrapSummary(AdminBootstrapResult Administrator, AdminBootstrapResult Player, IReadOnlyList<BootstrapCreatedAccount> CreatedAccounts);
public sealed class AdminBootstrapConflictException() : Exception("Bootstrap conflicts with an existing account; no account was promoted or restored.");

public sealed class AdminBootstrapService(UsersDbContext db, IPasswordHasher passwordHasher, IClock clock,
    IUserRegistrationEvents events, IOptions<AdminBootstrapOptions> settings)
{
    public async Task<AdminBootstrapSummary> ExecuteAsync(CancellationToken cancellationToken)
    {
        var options = settings.Value;
        if (!options.Enabled) return new(AdminBootstrapResult.Disabled, AdminBootstrapResult.Disabled, []);
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Bootstrap owns its startup transaction.");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"users:bootstrap"}, 0))", cancellationToken);
        List<BootstrapCreatedAccount> created = [];
        var admin = await EnsureAsync(options.Email!, options.Password!, UserRole.Administrator, "Administrator", created, cancellationToken);
        var player = await EnsureAsync(options.PlayerEmail!, options.PlayerPassword!, UserRole.User, "Player", created, cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, SchemaName: "public", TableName: "Users", ConstraintName: UserConfiguration.UniqueEmailIndexName })
        { throw new AdminBootstrapConflictException(); }
        return new(admin, player, created.AsReadOnly());
    }

    private async Task<AdminBootstrapResult> EnsureAsync(string address, string password, UserRole role, string name,
        List<BootstrapCreatedAccount> created, CancellationToken cancellationToken)
    {
        var email = Email.Create(address); PasswordPolicy.EnsureIsValid(password);
        var existing = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (existing is not null)
        {
            if (role == UserRole.Administrator && (existing.Role != UserRole.Administrator || !existing.IsActive || existing.IsDeleted))
                throw new AdminBootstrapConflictException();
            // Preserve the inherited player seed rule: an existing account is not edited, reactivated or demoted.
            return AdminBootstrapResult.AlreadyConfigured;
        }
        var hash = passwordHasher.Hash(password);
        var user = role == UserRole.Administrator ? User.RegisterAdministrator(name, email, hash, clock.UtcNow)
            : User.Register(name, email, hash, clock.UtcNow);
        db.Users.Add(user);
        await events.PublishCreatedAsync(user, cancellationToken);
        created.Add(new(user.Id, SensitiveDataMasker.MaskEmail(user.Email.Value)));
        return AdminBootstrapResult.Created;
    }
}
