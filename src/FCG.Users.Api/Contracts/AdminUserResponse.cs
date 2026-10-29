namespace FCG.Users.Api.Contracts;

public sealed record AdminUserResponse(Guid Id, string Name, string Email, string Role, bool IsActive,
    bool IsDeleted, DateTime CreatedAt, string Version);
