using System.Data;
using FCG.Users.Application.Common;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Users.Infrastructure.Identity;

public sealed class AdminUserReader(UsersDbContext db) : IAdminUserReader
{
    public async Task<PagedResult<AdminUserSummary>> SearchAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var term = search?.Trim();
        var query = string.IsNullOrEmpty(term) ? db.Database.SqlQueryRaw<AdminUserRow>(SelectSql) : db.Database.SqlQuery<AdminUserRow>($"""
            SELECT "Id", "Name", "Email", "Role", "IsActive", "IsDeleted", "CreatedAtUtc", xmin::text AS "Version"
            FROM "Users"
            WHERE strpos(lower("Name"), lower({term})) > 0 OR strpos(lower("Email"), lower({term})) > 0
            """);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(rows.Select(x => new AdminUserSummary(x.Id, x.Name, x.Email, Enum.Parse<UserRole>(x.Role),
            x.IsActive, x.IsDeleted, x.CreatedAtUtc, x.Version)).ToArray(), page, pageSize, total);
    }
    private const string SelectSql = """
        SELECT "Id", "Name", "Email", "Role", "IsActive", "IsDeleted", "CreatedAtUtc", xmin::text AS "Version" FROM "Users"
        """;
    private sealed class AdminUserRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Email { get; init; } = "";
        public string Role { get; init; } = "";
        public bool IsActive { get; init; }
        public bool IsDeleted { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string Version { get; init; } = "";
    }
}
