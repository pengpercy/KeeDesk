using System.Text;
using KeeDesk.Core.Crypto;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Services;

public static class DemoVaultService
{
    public static KdbxDatabase Create()
    {
        var database = KdbxDatabase.CreateDefault(L("Str_PersonalVault"));
        var root = database.RootGroup;
        var recycleBin = database.GetOrCreateRecycleBin();
        foreach (var group in root.Groups.Where(group => group != recycleBin))
            group.Dispose();
        root.Groups.Clear();

        var development = AddGroup(root, L("Str_DevelopmentAndOperations"), 3);
        development.IsExpanded = true;
        var code = AddGroup(development, L("Str_CodeHosting"), 3);
        var cloud = AddGroup(development, L("Str_CloudServices"), 3);
        var work = AddGroup(root, L("Str_Work"), 48);
        var personal = AddGroup(root, L("Str_Personal"), 1);
        var finance = AddGroup(root, L("Str_Finance"), 37);
        root.AddGroup(recycleBin);

        var latest = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var passwordOptions = new PasswordGeneratorOptions { Length = 24 };
        int count = 0;

        KdbxEntry Add(KdbxGroup group, string title, string user, string url, string? password = null, int iconId = 0)
        {
            var timestamp = latest.AddMinutes(-count++);
            var entry = new KdbxEntry
            {
                Title = title,
                IconId = iconId,
                UserName = user,
                Url = url,
                Password = password ?? PasswordGenerator.GenerateFromCharset(passwordOptions),
                CreationTime = timestamp,
                LastModificationTime = timestamp,
                LastAccessTime = timestamp,
                LocationChanged = timestamp,
            };
            group.AddEntry(entry);
            return entry;
        }

        var github = Add(code, "GitHub", "dev@example.com", "https://github.com", "demodemodemo", iconId: 62);
        github.Notes = L("Str_OrganizationRepositoryAccountRecoveryCodesAreAttached");
        github.Tags.AddRange([L("Str_Development"), L("Str_Team")]);
        github.IsFavorite = true;
        SetDemoTotp(github, "GitHub", "JBSWY3DPEHPK3PXP");
        github.Binaries[L("Str_RecoveryCodesTxt")] = new ProtectedBinary(true,
            Encoding.UTF8.GetBytes(L("Str_DemoRecoveryCodesNDEMO48215216NDEMO")));

        Add(work, "Figma", "design@example.com", "https://www.figma.com", iconId: 23);
        Add(work, "Microsoft 365", "work@example.com", "https://www.microsoft365.com", iconId: 19).IsFavorite = true;
        var cloudflare = Add(cloud, "Cloudflare", "infra@example.com", "https://dash.cloudflare.com", "cloud", iconId: 3);
        SetDemoTotp(cloudflare, "Cloudflare", "NB2W45DFOIZA");
        Add(personal, "Synology NAS", "admin", "https://nas.example.com", "nas1", iconId: 3);
        Add(finance, L("Str_BankAccount"), L("Str_PersonalAccount"), "https://bank.example.com", iconId: 37);
        Add(personal, "Proton Mail", "personal@example.com", "https://mail.proton.me", iconId: 19);
        Add(personal, "Tailscale", "home@example.com", "https://login.tailscale.com", iconId: 1);
        Add(personal, "Bitwarden", "personal@example.com", "https://vault.bitwarden.com", iconId: 56);
        Add(development, L("Str_DeveloperDocs"), "docs@example.com", "https://docs.example.com", iconId: 61);

        var samples = new (KdbxGroup Group, int DirectCount, string Prefix)[]
        {
            (code, 5, L("Str_Repository")), (cloud, 12, L("Str_CloudServices")), (development, 15, L("Str_OperationsTool")),
            (work, 36, L("Str_WorkAccount")), (personal, 42, L("Str_PersonalAccount")), (finance, 18, L("Str_FinancialAccount")),
        };
        string reusedPassword = PasswordGenerator.GenerateFromCharset(passwordOptions);
        foreach (var (group, target, prefix) in samples)
        {
            for (int row = group.Entries.Count + 1; row <= target; row++)
            {
                int index = count;
                var sample = Add(group, $"{prefix} {row:00}", $"sample-{index + 1:000}@example.com",
                    $"https://account-{index + 1:000}.example.com", index < 16 ? reusedPassword : null);
                sample.IsFavorite = index < 20;
                if (index < 16)
                    SetDemoTotp(sample, "KeeDesk Demo", "KRSXG5DSN5ZGK3TPOI");
                if (index < 12)
                {
                    sample.Expires = true;
                    sample.ExpiryTime = DateTimeOffset.UtcNow.AddDays(-1);
                }
            }
        }

        database.IsModified = false;
        return database;
    }

    public static KdbxDatabase CreateWorkVault(KdbxDatabase personalDemo)
    {
        var vault = KdbxDatabase.CreateDefault(L("Str_WorkVault"));
        var archive = vault.RootGroup.Groups[0];
        archive.Name = L("Str_Work");
        var source = personalDemo.RootGroup.Groups.Single(group => group.Name == L("Str_Work"));
        foreach (var entry in source.Entries)
        {
            var copy = entry.CloneWithoutHistory();
            copy.Uuid = Guid.NewGuid();
            archive.AddEntry(copy);
        }
        return vault;
    }

    private static KdbxGroup AddGroup(KdbxGroup parent, string name, int iconId)
    {
        var group = new KdbxGroup { Name = name, IconId = iconId };
        parent.AddGroup(group);
        return group;
    }

    private static void SetDemoTotp(KdbxEntry entry, string issuer, string secret)
    {
        var provider = Uri.EscapeDataString(issuer);
        var user = Uri.EscapeDataString(entry.UserName);
        entry.SetString(KdbxConstants.FieldOtp, $"otpauth://totp/{provider}:{user}?secret={secret}&issuer={provider}", true);
    }
}
