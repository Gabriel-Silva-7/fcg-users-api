namespace FCG.Users.Api.Contracts;

public sealed record CurrentUserResponse(Guid Id, string Name, string Email, string Role);
