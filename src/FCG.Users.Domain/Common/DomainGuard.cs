namespace FCG.Users.Domain.Common;

internal static class DomainGuard
{
    public static void EnsureUtc(DateTime value, string paramName)
    {
        if (value.Kind is not DateTimeKind.Utc)
        {
            throw new ArgumentException($"{paramName} must be in UTC.", paramName);
        }
    }
}
