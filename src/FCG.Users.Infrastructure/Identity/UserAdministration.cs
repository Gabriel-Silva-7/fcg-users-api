using System.Data;
using System.Globalization;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Users.Infrastructure.Identity;

/// <summary>Each command reads the current row under a transaction lock; no stale entity is saved over a concurrent edit.</summary>
public sealed class UserAdministration(UsersDbContext db, IUserIdentityEvents events) : IUserAdministration
{
    public async Task<ChangeUserStatusResult> ChangeStatusAsync(Guid userId, bool isActive, uint expectedVersion, CancellationToken cancellationToken)
    {
        EnsureOwnTransaction();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var user = await LockAsync(userId, cancellationToken);
        if (user is null) return new(ChangeUserStatusStatus.NotFound);
        var entry = db.Entry(user);
        if (entry.Property<uint>("xmin").CurrentValue != expectedVersion) return new(ChangeUserStatusStatus.ConcurrencyConflict);
        if (user.IsDeleted) return new(ChangeUserStatusStatus.UserDeleted);
        var originalVersion = user.StateVersion;
        user.ChangeActiveStatus(isActive);
        // Preserve Fase1: successful status no-op still renews the opaque HTTP concurrency token.
        entry.Property(x => x.IsActive).IsModified = true;
        try
        {
            if (user.StateVersion != originalVersion) await events.PublishChangedAsync(user, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(ChangeUserStatusStatus.ConcurrencyConflict);
        }
        return new(ChangeUserStatusStatus.Updated, new(user.Id, user.Name, user.Email.Value, user.Role,
            user.IsActive, user.IsDeleted, user.CreatedAtUtc, entry.Property<uint>("xmin").CurrentValue.ToString(CultureInfo.InvariantCulture)));
    }

    public async Task<DeleteUserResult> DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        EnsureOwnTransaction();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var user = await LockAsync(userId, cancellationToken);
        if (user is null) return new(DeleteUserStatus.NotFound);
        if (!user.SoftDelete()) return new(DeleteUserStatus.Deleted);
        await events.PublishChangedAsync(user, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(DeleteUserStatus.Deleted, Changed: true);
    }

    private Task<User?> LockAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.FromSqlInterpolated($"SELECT *, xmin FROM \"Users\" WHERE \"Id\"={id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private void EnsureOwnTransaction()
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Account administration owns its transaction.");
        db.ChangeTracker.Clear();
    }
}
