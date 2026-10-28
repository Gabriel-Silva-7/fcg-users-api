using Xunit;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Domain.Tests.Identity;

public sealed class UserTests
{
    private const string ValidPasswordHash = "hashed-password";

    private static readonly DateTime ValidCreatedAtUtc = DateTime.UnixEpoch;

    [Fact]
    public void Register_WhenDataIsValid_CreatesActiveUserWithUserRole()
    {
        var email = Email.Create("user@example.com");

        var user = User.Register(
            "Gabriel Silva",
            email,
            ValidPasswordHash,
            ValidCreatedAtUtc);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("Gabriel Silva", user.Name);
        Assert.Same(email, user.Email);
        Assert.Equal(ValidPasswordHash, user.PasswordHash);
        Assert.Equal(UserRole.User, user.Role);
        Assert.True(user.IsActive);
        Assert.Equal(ValidCreatedAtUtc, user.CreatedAtUtc);
    }

    [Fact]
    public void RegisterAdministrator_WhenDataIsValid_CreatesActiveAdministrator()
    {
        var user = User.RegisterAdministrator(
            "Administrator",
            Email.Create("admin@example.com"),
            ValidPasswordHash,
            ValidCreatedAtUtc);

        Assert.Equal("Administrator", user.Name);
        Assert.Equal(UserRole.Administrator, user.Role);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void ChangeActiveStatus_ChangesTheAccountState()
    {
        var user = RegisterUser();

        user.ChangeActiveStatus(false);

        Assert.False(user.IsActive);

        user.ChangeActiveStatus(true);

        Assert.True(user.IsActive);
    }

    [Fact]
    public void UpdateProfile_WhenDataIsValid_ChangesNameAndEmail()
    {
        var user = RegisterUser();
        var email = Email.Create("updated@example.com");

        user.UpdateProfile("  Gabriel Updated  ", email);

        Assert.Equal("Gabriel Updated", user.Name);
        Assert.Same(email, user.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateProfile_WhenNameIsEmpty_ThrowsArgumentException(string name)
    {
        var user = RegisterUser();

        var exception = Assert.Throws<ArgumentException>(
            () => user.UpdateProfile(name, Email.Create("updated@example.com")));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void ChangePasswordHash_WhenHashIsValid_ChangesPasswordHash()
    {
        var user = RegisterUser();

        user.ChangePasswordHash("updated-password-hash");

        Assert.Equal("updated-password-hash", user.PasswordHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangePasswordHash_WhenHashIsEmpty_ThrowsArgumentException(string passwordHash)
    {
        var user = RegisterUser();

        var exception = Assert.Throws<ArgumentException>(
            () => user.ChangePasswordHash(passwordHash));

        Assert.Equal("passwordHash", exception.ParamName);
    }

    [Fact]
    public void Register_TrimsName()
    {
        var user = RegisterUser(name: "  Gabriel Silva  ");

        Assert.Equal("Gabriel Silva", user.Name);
    }

    [Fact]
    public void Register_WhenNameIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => RegisterUser(name: null!));

        Assert.Equal("name", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WhenNameIsEmpty_ThrowsArgumentException(string name)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RegisterUser(name: name));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Register_WhenNameExceedsMaximumLength_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RegisterUser(name: new string('a', User.MaxNameLength + 1)));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Register_WhenNameHasMaximumLength_AcceptsName()
    {
        var name = new string('a', User.MaxNameLength);

        var user = RegisterUser(name: name);

        Assert.Equal(name, user.Name);
    }

    [Fact]
    public void Register_WhenEmailIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => User.Register(
                "Gabriel Silva",
                null!,
                ValidPasswordHash,
                ValidCreatedAtUtc));

        Assert.Equal("email", exception.ParamName);
    }

    [Fact]
    public void Register_WhenPasswordHashIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => RegisterUser(passwordHash: null!));

        Assert.Equal("passwordHash", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WhenPasswordHashIsEmpty_ThrowsArgumentException(string passwordHash)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RegisterUser(passwordHash: passwordHash));

        Assert.Equal("passwordHash", exception.ParamName);
    }

    [Fact]
    public void Register_WhenPasswordHashExceedsMaximumLength_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RegisterUser(
                passwordHash: new string('a', User.MaxPasswordHashLength + 1)));

        Assert.Equal("passwordHash", exception.ParamName);
    }

    [Fact]
    public void Register_WhenPasswordHashHasMaximumLength_AcceptsHash()
    {
        var passwordHash = new string('a', User.MaxPasswordHashLength);

        var user = RegisterUser(passwordHash: passwordHash);

        Assert.Equal(passwordHash, user.PasswordHash);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Register_WhenCreationDateIsNotUtc_ThrowsArgumentException(DateTimeKind kind)
    {
        var createdAt = DateTime.SpecifyKind(ValidCreatedAtUtc, kind);

        var exception = Assert.Throws<ArgumentException>(
            () => RegisterUser(createdAtUtc: createdAt));

        Assert.Equal("createdAtUtc", exception.ParamName);
    }

    [Fact]
    public void User_HasNoPublicConstructor()
    {
        Assert.Empty(typeof(User).GetConstructors());
    }

    [Fact]
    public void User_PropertiesHaveNoPublicSetters()
    {
        Assert.All(
            typeof(User).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
    }

    [Fact]
    public void User_DoesNotExposePlainTextPasswordProperty()
    {
        Assert.Null(typeof(User).GetProperty("Password"));
    }

    [Fact]
    public void UserRole_DefinesUserAndAdministratorLevels()
    {
        Assert.Equal(
            [UserRole.User, UserRole.Administrator],
            Enum.GetValues<UserRole>());
    }

    private static User RegisterUser(
        string name = "Gabriel Silva",
        Email? email = null,
        string passwordHash = ValidPasswordHash,
        DateTime? createdAtUtc = null) =>
        User.Register(
            name,
            email ?? Email.Create("user@example.com"),
            passwordHash,
            createdAtUtc ?? ValidCreatedAtUtc);
}
