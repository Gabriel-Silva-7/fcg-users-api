using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Persistence;
using FCG.Users.Infrastructure.Persistence.Configurations.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace FCG.Users.Infrastructure.Identity;
public sealed class UserRepository(UsersDbContext db) : IUserRepository
{
    public Task<bool> ExistsByEmailAsync(Email email, CancellationToken cancellationToken) => db.Users.AnyAsync(x => x.Email == email, cancellationToken);
    public Task<User?> FindByEmailAsync(Email email, CancellationToken cancellationToken) => db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, cancellationToken);
    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) => db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    public void Add(User user) => db.Users.Add(user);
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: UserConfiguration.UniqueEmailIndexName })
        {
            throw new EmailAlreadyRegisteredException(exception);
        }
    }
}
