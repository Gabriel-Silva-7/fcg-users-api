using FCG.Users.Application.Common;

namespace FCG.Users.Infrastructure.Common;

public sealed class SystemClock(TimeProvider clock) : IClock
{
    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
}
