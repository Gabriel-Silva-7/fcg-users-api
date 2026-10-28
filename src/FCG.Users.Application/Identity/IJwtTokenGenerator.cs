using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public interface IJwtTokenGenerator
{
    AccessToken Generate(User user);
}
