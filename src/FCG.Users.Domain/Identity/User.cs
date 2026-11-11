using FCG.Users.Domain.Common;

namespace FCG.Users.Domain.Identity;

public sealed class User
{
    public const int MaxNameLength = 120;
    public const int MaxPasswordHashLength = 256;

    private User(
        Guid id,
        string name,
        Email email,
        string passwordHash,
        UserRole role,
        bool isActive,
        DateTime createdAtUtc)
    {
        Id = id;
        Name = name;
        Email = email;
        PasswordHash = passwordHash;
        Role = role;
        IsActive = isActive;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public Email Email { get; private set; }

    public string PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsDeleted { get; private set; }

    public long StateVersion { get; private set; } = 1;

    public DateTime CreatedAtUtc { get; private set; }

    public static User Register(
        string name,
        Email email,
        string passwordHash,
        DateTime createdAtUtc) =>
        Create(name, email, passwordHash, UserRole.User, createdAtUtc);

    public static User RegisterAdministrator(
        string name,
        Email email,
        string passwordHash,
        DateTime createdAtUtc) =>
        Create(name, email, passwordHash, UserRole.Administrator, createdAtUtc);

    public void ChangeActiveStatus(bool isActive)
    {
        if (IsDeleted) throw new InvalidOperationException("A deleted account cannot change status.");
        if (IsActive == isActive) return;
        var nextVersion = checked(StateVersion + 1);
        IsActive = isActive;
        StateVersion = nextVersion;
    }

    public bool SoftDelete()
    {
        if (IsDeleted) return false;
        var nextVersion = checked(StateVersion + 1);
        IsActive = false;
        IsDeleted = true;
        StateVersion = nextVersion;
        return true;
    }

    public void UpdateProfile(string name, Email email)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (!IsActive || IsDeleted) throw new InvalidOperationException("Only an active account can edit its profile.");
        var normalized = NormalizeName(name);
        var changed = Name != normalized || Email != email;
        var nextVersion = changed ? checked(StateVersion + 1) : StateVersion;
        Name = normalized;
        Email = email;
        StateVersion = nextVersion;
    }

    public void ChangePasswordHash(string passwordHash)
    {
        EnsureValidPasswordHash(passwordHash);
        if (!IsActive || IsDeleted) throw new InvalidOperationException("Only an active account can change its password.");
        PasswordHash = passwordHash;
    }

    private static User Create(
        string name,
        Email email,
        string passwordHash,
        UserRole role,
        DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(email);
        var normalizedName = NormalizeName(name);
        EnsureValidPasswordHash(passwordHash);

        DomainGuard.EnsureUtc(createdAtUtc, nameof(createdAtUtc));

        return new User(
            Guid.NewGuid(),
            normalizedName,
            email,
            passwordHash,
            role,
            true,
            new DateTime(createdAtUtc.Ticks - createdAtUtc.Ticks % 10, DateTimeKind.Utc));
    }

    private static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var normalizedName = name.Trim();

        if (normalizedName.Length is 0 or > MaxNameLength)
        {
            throw new ArgumentException(
                $"Name must contain between 1 and {MaxNameLength} characters.",
                nameof(name));
        }

        return normalizedName;
    }

    private static void EnsureValidPasswordHash(string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash cannot be empty.", nameof(passwordHash));
        }

        if (passwordHash.Length > MaxPasswordHashLength)
        {
            throw new ArgumentException(
                $"Password hash cannot exceed {MaxPasswordHashLength} characters.",
                nameof(passwordHash));
        }
    }
}
