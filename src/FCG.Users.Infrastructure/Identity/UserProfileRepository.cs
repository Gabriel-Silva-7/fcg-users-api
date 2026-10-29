using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Persistence;
using FCG.Users.Infrastructure.Persistence.Configurations.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FCG.Users.Infrastructure.Identity;

public sealed class UserProfileRepository(UsersDbContext db) : IUserProfileRepository
{
    public Task<User?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.TagWith("profile-edit-load").SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);

    public async Task<ProfileSaveStatus> SaveAsync(User user, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Profile persistence owns its transaction.");
        if (db.Entry(user).State == EntityState.Detached) throw new InvalidOperationException("Profile writes require the tracked original snapshot.");
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Preserve an HTTP concurrency checkpoint for no-op edits as well, without advancing snapshot version.
            db.Entry(user).Property(x => x.Name).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ProfileSaveStatus.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The failed transaction has been disposed/rolled back before reading the committed current state.
            db.ChangeTracker.Clear();
            var current = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == user.Id, cancellationToken);
            return current is null || !current.IsActive || current.IsDeleted ? ProfileSaveStatus.NotActive : ProfileSaveStatus.ConcurrencyConflict;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, SchemaName: "public", TableName: "Users", ConstraintName: UserConfiguration.UniqueEmailIndexName })
        {
            db.ChangeTracker.Clear();
            return ProfileSaveStatus.EmailAlreadyRegistered;
        }
    }
}
