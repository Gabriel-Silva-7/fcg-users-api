namespace FCG.Users.Application.Identity;

/// <summary>Identity could not be read; distinct from an absent account or invalid credential.</summary>
public sealed class IdentityStorageUnavailableException(Exception innerException)
    : Exception("Identity storage is unavailable.", innerException);
