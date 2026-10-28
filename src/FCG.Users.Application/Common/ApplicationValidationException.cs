namespace FCG.Users.Application.Common;

public sealed class ApplicationValidationException : Exception
{
    public ApplicationValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
