using Xunit;
using FCG.Users.Application.Common;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Api.Tests.Inherited;

public sealed class ChangeOwnPasswordHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenCurrentPasswordMatches_StoresTheNewHash()
    {
        var user = RegisterUser();
        var repository = new FakeUserRepository(user);
        var hasher = new FakePasswordHasher(matches: true);
        var handler = new ChangeOwnPasswordHandler(repository, hasher);

        var result = await handler.HandleAsync(
            new ChangeOwnPasswordCommand(user.Id, "Current!1", "Updated!2"),
            CancellationToken.None);

        Assert.Equal(ChangeOwnPasswordStatus.Updated, result.Status);
        Assert.Equal("hash:Updated!2", user.PasswordHash);
        Assert.True(repository.SaveCalled);
    }

    [Fact]
    public async Task HandleAsync_WhenCurrentPasswordDoesNotMatch_DoesNotSave()
    {
        var repository = new FakeUserRepository(RegisterUser());
        var handler = new ChangeOwnPasswordHandler(
            repository,
            new FakePasswordHasher(matches: false));

        var result = await handler.HandleAsync(
            new ChangeOwnPasswordCommand(Guid.NewGuid(), "Wrong!1", "Updated!2"),
            CancellationToken.None);

        Assert.Equal(ChangeOwnPasswordStatus.InvalidCurrentPassword, result.Status);
        Assert.False(repository.SaveCalled);
    }

    [Fact]
    public async Task HandleAsync_WhenNewPasswordIsWeak_ReturnsValidationErrorBeforePersistence()
    {
        var repository = new FakeUserRepository(RegisterUser());
        var handler = new ChangeOwnPasswordHandler(repository, new FakePasswordHasher(matches: true));

        var exception = await Assert.ThrowsAsync<ApplicationValidationException>(
            () => handler.HandleAsync(
                new ChangeOwnPasswordCommand(Guid.NewGuid(), "Current!1", "12345678"),
                CancellationToken.None));

        Assert.Contains(nameof(ChangeOwnPasswordCommand.NewPassword), exception.Errors.Keys);
        Assert.False(repository.SaveCalled);
    }

    [Fact]
    public async Task Too_long_current_password_is_rejected_before_any_password_write()
    {
        var repository = new FakeUserRepository(RegisterUser());
        var handler = new ChangeOwnPasswordHandler(repository, new FakePasswordHasher(matches: true));
        var error = await Assert.ThrowsAsync<ApplicationValidationException>(() => handler.HandleAsync(
            new(Guid.NewGuid(), new string('x',129), "ValidFixture1!"), CancellationToken.None));
        Assert.Contains(nameof(ChangeOwnPasswordCommand.CurrentPassword), error.Errors.Keys);
        Assert.False(repository.SaveCalled);
    }

    private static User RegisterUser() =>
        User.Register(
            "User",
            Email.Create("user@example.com"),
            "old-hash",
            DateTime.UnixEpoch);

    private sealed class FakePasswordHasher(bool matches) : IPasswordHasher
    {
        public string Hash(string password) => $"hash:{password}";

        public bool Verify(string? passwordHash, string password) => matches;
    }

    private sealed class FakeUserRepository(User? user) : IUserSelfRepository
    {
        public bool SaveCalled { get; private set; }

        public Task<User?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(user);

        public Task<SelfSaveStatus> SaveAsync(User user, CancellationToken cancellationToken)
        {
            SaveCalled = true;
            return Task.FromResult(SelfSaveStatus.Saved);
        }
    }
}
