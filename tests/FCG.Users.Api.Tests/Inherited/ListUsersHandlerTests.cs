using Xunit;
using FCG.Users.Application.Common;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Api.Tests.Inherited;

public sealed class ListUsersHandlerTests
{
    // A validação não pode viver só nas DataAnnotations do request: uma chamada direta ao caso de
    // uso chegaria ao banco com OFFSET negativo e estouraria como 500.
    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1000001, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task HandleAsync_WithPaginationOutsideTheContract_ThrowsWithoutQuerying(
        int page,
        int pageSize)
    {
        var repository = new FakeUserRepository();
        var handler = new ListUsersHandler(repository);

        var exception = await Assert.ThrowsAsync<ApplicationValidationException>(() =>
            handler.HandleAsync(new ListUsersQuery(null, page, pageSize), CancellationToken.None));

        Assert.NotEmpty(exception.Errors);
        Assert.False(repository.WasQueried);
    }

    [Fact]
    public async Task HandleAsync_WithinTheContract_DelegatesToTheRepository()
    {
        var repository = new FakeUserRepository();
        var handler = new ListUsersHandler(repository);

        var result = await handler.HandleAsync(
            new ListUsersQuery("gabriel", 2, 50),
            CancellationToken.None);

        Assert.True(repository.WasQueried);
        Assert.Equal("gabriel", repository.LastSearch);
        Assert.Equal(2, repository.LastPage);
        Assert.Equal(50, repository.LastPageSize);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task HandleAsync_WithSearchOutsideTheContract_ThrowsWithoutQuerying()
    {
        var repository = new FakeUserRepository();
        var handler = new ListUsersHandler(repository);

        var exception = await Assert.ThrowsAsync<ApplicationValidationException>(() =>
            handler.HandleAsync(
                new ListUsersQuery(
                    new string('x', ListUsersQuery.MaxSearchLength + 1),
                    ListUsersQuery.MinPage,
                    ListUsersQuery.DefaultPageSize),
                CancellationToken.None));

        Assert.Contains(nameof(ListUsersQuery.Search), exception.Errors.Keys);
        Assert.False(repository.WasQueried);
    }

    private sealed class FakeUserRepository : IAdminUserReader
    {
        public bool WasQueried { get; private set; }

        public string? LastSearch { get; private set; }

        public int LastPage { get; private set; }

        public int LastPageSize { get; private set; }

        public Task<PagedResult<AdminUserSummary>> SearchAsync(
            string? search,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            WasQueried = true;
            LastSearch = search;
            LastPage = page;
            LastPageSize = pageSize;

            return Task.FromResult(new PagedResult<AdminUserSummary>([], page, pageSize, 0));
        }

    }
}
