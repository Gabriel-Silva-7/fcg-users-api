namespace FCG.Users.Application.Identity;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string? passwordHash, string password);
}
