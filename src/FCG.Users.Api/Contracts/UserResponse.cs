namespace FCG.Users.Api.Contracts;

public sealed record UserResponse(Guid Id, string Name, string Email, string Role);
