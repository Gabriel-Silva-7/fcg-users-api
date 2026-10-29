using System.Data;
using FCG.Contracts;
using FCG.Users.Application.Identity;
using FCG.Users.Infrastructure.Messaging;
using FCG.Users.Infrastructure.Persistence;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Users.Infrastructure.Identity;

public sealed record IdentityReconciliationReport(bool DryRun, int Selected, int Staged, int Active, int Blocked, int Deleted, bool NotFound);

/// <summary>One coherent read snapshot and one commit of recovery intents; a fresh outbox scope per invocation.</summary>
public sealed class IdentityReconciler(IServiceScopeFactory scopes)
{
    public async Task<IdentityReconciliationReport> ExecuteAsync(Guid? userId, bool dryRun, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty) throw new ArgumentException("Reconciliation requires a nonempty identity.", nameof(userId));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var snapshots = scope.ServiceProvider.GetRequiredService<UserIdentitySnapshotFactory>();
        var events = scope.ServiceProvider.GetRequiredService<IUserIdentityEvents>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (dryRun) await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        // Validate the whole storage shape even for a dry run/empty selection; never run migrations here.
        await db.Users.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
        await db.Set<OutboxMessage>().AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
        await db.Set<OutboxState>().AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
        var selected = 0; var active = 0; var blocked = 0; var deleted = 0; var offset = 0;
        var source = db.Users.AsNoTracking().TagWith("identity-reconciliation-page").AsQueryable();
        if (userId is { } id) source = source.Where(user => user.Id == id);
        while (true)
        {
            var rows = await source.OrderBy(user => user.Id).Skip(offset).Take(100).ToArrayAsync(cancellationToken);
            foreach (var user in rows)
            {
                var snapshot = snapshots.Snapshot(user); // Validate canonical content in dry runs as well.
                selected = checked(selected + 1);
                switch (snapshot.State)
                {
                    case IdentityState.Active: active++; break;
                    case IdentityState.Blocked: blocked++; break;
                    case IdentityState.Deleted: deleted++; break;
                }
                if (!dryRun) await events.PublishSnapshotAsync(snapshot, cancellationToken);
            }
            if (!dryRun && rows.Length > 0) await db.SaveChangesAsync(cancellationToken);
            if (rows.Length < 100) break;
            offset = checked(offset + rows.Length);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(dryRun, selected, dryRun ? 0 : selected, active, blocked, deleted, userId.HasValue && selected == 0);
    }
}
