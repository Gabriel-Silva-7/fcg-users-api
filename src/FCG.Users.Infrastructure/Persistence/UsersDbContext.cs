using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Persistence.Configurations.Identity;
using Microsoft.EntityFrameworkCore;
namespace FCG.Users.Infrastructure.Persistence;
public sealed class UsersDbContext(DbContextOptions<UsersDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    protected override void OnModelCreating(ModelBuilder builder) => builder.ApplyConfiguration(new UserConfiguration());
}
