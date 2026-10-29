using Xunit;
using FCG.Users.Application.Common;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Api.Tests.Inherited;

public sealed class GetCurrentUserHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenUserExists_ReturnsOnlyProfileData()
    {
        var user = User.Register(
            "Gabriel",
            Email.Create("gabriel@example.com"),
            "HASHED_PASSWORD",
            DateTime.UnixEpoch);
        var repository = new FakeUserRepository(user);
        var handler = new GetCurrentUserHandler(repository);

        var result = await handler.HandleAsync(user.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
        Assert.Equal("Gabriel", result.Name);
        Assert.Equal("gabriel@example.com", result.Email);
        Assert.Equal(UserRole.User, result.Role);
        Assert.Equal(user.Id, repository.LastUserId);
    }

    [Fact]
    public async Task HandleAsync_WhenUserDoesNotExist_ReturnsNull()
    {
        var handler = new GetCurrentUserHandler(new FakeUserRepository(user: null));

        var result = await handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class FakeUserRepository(User? user) : IUserRepository
    {
        public Guid? LastUserId { get; private set; }

        public Task<bool> ExistsByEmailAsync(
            Email email,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<User?> FindByEmailAsync(
            Email email,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<User?> FindByIdAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            LastUserId = id;
            return Task.FromResult(user);
        }

        public void Add(User newUser) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
