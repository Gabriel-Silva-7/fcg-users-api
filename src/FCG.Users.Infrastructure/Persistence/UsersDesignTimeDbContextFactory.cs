using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace FCG.Users.Infrastructure.Persistence;
public sealed class UsersDesignTimeDbContextFactory : IDesignTimeDbContextFactory<UsersDbContext>
{
    public UsersDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<UsersDbContext>().UseNpgsql(
        Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? "Host=localhost;Database=fcg_users;Username=design_time").Options);
}
