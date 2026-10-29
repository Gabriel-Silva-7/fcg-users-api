namespace FCG.Users.Infrastructure.Identity;

public sealed class AdminBootstrapOptions
{
    public const string SectionName = "AdminBootstrap";
    public bool Enabled { get; set; }
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? PlayerEmail { get; set; }
    public string? PlayerPassword { get; set; }
}
