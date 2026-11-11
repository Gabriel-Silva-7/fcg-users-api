using FCG.Users.Application.Common;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public sealed record ListUsersQuery(string? Search, int Page, int PageSize)
{
    public const int MinPage = 1;

    // Mantém (page - 1) * pageSize dentro do intervalo de int usado pelo Skip.
    public const int MaxPage = 1_000_000;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;
    public const int MaxSearchLength = 256;
}

public interface IAdminUserReader
{
    Task<PagedResult<AdminUserSummary>> SearchAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed class ListUsersHandler(IAdminUserReader users)
{
    public Task<PagedResult<AdminUserSummary>> HandleAsync(
        ListUsersQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Validate(query);

        return users.SearchAsync(
            query.Search,
            query.Page,
            query.PageSize,
            cancellationToken);
    }

    // A validação vive aqui, e não só nas DataAnnotations do request: sem ela, uma chamada direta
    // ao caso de uso (job, CLI, outro controller) chega ao banco com OFFSET negativo e estoura.
    private static void Validate(ListUsersQuery query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (query.Page is < ListUsersQuery.MinPage or > ListUsersQuery.MaxPage)
        {
            errors[nameof(query.Page)] =
                [$"Page must be between {ListUsersQuery.MinPage} and {ListUsersQuery.MaxPage}."];
        }

        if (query.PageSize is < ListUsersQuery.MinPageSize or > ListUsersQuery.MaxPageSize)
        {
            errors[nameof(query.PageSize)] =
                [$"Page size must be between {ListUsersQuery.MinPageSize} and {ListUsersQuery.MaxPageSize}."];
        }

        if (query.Search?.Length > ListUsersQuery.MaxSearchLength)
        {
            errors[nameof(query.Search)] =
                [$"Search cannot exceed {ListUsersQuery.MaxSearchLength} characters."];
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }
    }
}
