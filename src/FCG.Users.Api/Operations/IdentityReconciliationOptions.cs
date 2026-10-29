namespace FCG.Users.Api.Operations;

public sealed record IdentityReconciliationOptions(Guid? UserId, bool DryRun)
{
    public const string Usage = "Usage: reconcile-identities (--all | --user-id GUID) [--dry-run]";
    public static bool TryParse(string[] args, out IdentityReconciliationOptions? options)
    {
        options = null;
        var selected = false; var dryRun = false; Guid? userId = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--all" when !selected: selected = true; break;
                case "--user-id" when !selected && index + 1 < args.Length:
                    if (!Guid.TryParse(args[++index], out var id) || id == Guid.Empty) return false;
                    selected = true; userId = id; break;
                case "--dry-run" when !dryRun: dryRun = true; break;
                default: return false;
            }
        }
        if (!selected) return false;
        options = new(userId, dryRun);
        return true;
    }
}
