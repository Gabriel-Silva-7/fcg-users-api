using Xunit;
using FCG.Users.Application.Common;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Api.Tests.Inherited;

public sealed class UpdateCurrentUserHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenDataIsValid_UpdatesAndReturnsTheProfile()
    {
        var user = RegisterUser();
        var repository = new FakeUserRepository(user);
        var handler = new UpdateCurrentUserHandler(repository);

        var result = await handler.HandleAsync(
            new UpdateCurrentUserCommand(user.Id, "  Updated Name  ", "UPDATED@EXAMPLE.COM"),
            CancellationToken.None);

        Assert.Equal(UpdateCurrentUserStatus.Updated, result.Status);
        Assert.Equal("Updated Name", result.User!.Name);
        Assert.Equal("updated@example.com", result.User.Email);
        Assert.True(repository.SaveCalled);
    }

    [Fact]
    public async Task HandleAsync_WhenEmailAlreadyExists_ReturnsConflict()
    {
        var repository = new FakeUserRepository(RegisterUser(), throwDuplicate: true);
        var handler = new UpdateCurrentUserHandler(repository);

        var result = await handler.HandleAsync(
            new UpdateCurrentUserCommand(Guid.NewGuid(), "Updated", "used@example.com"),
            CancellationToken.None);

        Assert.Equal(UpdateCurrentUserStatus.EmailAlreadyRegistered, result.Status);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task HandleAsync_WhenNameAndEmailAreInvalid_ReturnsBothValidationErrors()
    {
        var handler = new UpdateCurrentUserHandler(new FakeUserRepository(RegisterUser()));

        var exception = await Assert.ThrowsAsync<ApplicationValidationException>(
            () => handler.HandleAsync(
                new UpdateCurrentUserCommand(Guid.NewGuid(), "   ", "invalid"),
                CancellationToken.None));

        Assert.Contains(nameof(UpdateCurrentUserCommand.Name), exception.Errors.Keys);
        Assert.Contains(nameof(UpdateCurrentUserCommand.Email), exception.Errors.Keys);
    }

    private static User RegisterUser() =>
        User.Register(
            "Original",
            Email.Create("original@example.com"),
            "password-hash",
            DateTime.UnixEpoch);

    private sealed class FakeUserRepository(User? user, bool throwDuplicate = false)
        : IUserProfileRepository
    {
        public bool SaveCalled { get; private set; }

        public Task<User?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(user);

        public Task<ProfileSaveStatus> SaveAsync(User saved, CancellationToken cancellationToken)
        {
            SaveCalled = true;
            return Task.FromResult(throwDuplicate ? ProfileSaveStatus.EmailAlreadyRegistered : ProfileSaveStatus.Saved);
        }
    }
}
