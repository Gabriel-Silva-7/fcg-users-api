namespace FCG.Users.Application.Common;

public interface IClock
{
    DateTime UtcNow { get; }
}
