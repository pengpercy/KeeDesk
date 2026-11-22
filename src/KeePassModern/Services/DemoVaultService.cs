using System.Text;
using KeePassModern.Core.Crypto;
using KeePassModern.Core.Kdbx;
using KeePassModern.Core.Services;

namespace KeePassModern.Services;

public static class DemoVaultService
{
    public static KdbxDatabase Create()
    {
        var database = KdbxDatabase.CreateDefault("个人密码库");
        var root = database.RootGroup;
        var recycleBin = database.GetOrCreateRecycleBin();
        foreach (var group in root.Groups.Where(group => group != recycleBin))
            group.Dispose();
        root.Groups.Clear();

        var development = AddGroup(root, "开发与运维", 3);
        development.IsExpanded = true;
        var code = AddGroup(development, "代码托管", 3);
        var cloud = AddGroup(development, "云服务", 3);
        var work = AddGroup(root, "工作", 48);
        var personal = AddGroup(root, "个人", 1);
        var finance = AddGroup(root, "金融", 37);
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
        github.Notes = "组织仓库账户。恢复码存放在附件中。";
        github.Tags.AddRange(["开发", "团队"]);
        github.IsFavorite = true;
        SetDemoTotp(github, "GitHub", "JBSWY3DPEHPK3PXP");
        github.Binaries["恢复码.txt"] = new ProtectedBinary(true,
            Encoding.UTF8.GetBytes("演示恢复码\nDEMO-4821-5216\nDEMO-6073-1184"));

        Add(work, "Figma", "design@example.com", "https://www.figma.com", iconId: 23);
        Add(work, "Microsoft 365", "work@example.com", "https://www.microsoft365.com", iconId: 19).IsFavorite = true;
        var cloudflare = Add(cloud, "Cloudflare", "infra@example.com", "https://dash.cloudflare.com", "cloud", iconId: 3);
        SetDemoTotp(cloudflare, "Cloudflare", "NB2W45DFOIZA");
        Add(personal, "Synology NAS", "admin", "https://nas.example.com", "nas1", iconId: 3);
        Add(finance, "银行账户", "个人账户", "https://bank.example.com", iconId: 37);
        Add(personal, "Proton Mail", "personal@example.com", "https://mail.proton.me", iconId: 19);
        Add(personal, "Tailscale", "home@example.com", "https://login.tailscale.com", iconId: 1);
        Add(personal, "Bitwarden", "personal@example.com", "https://vault.bitwarden.com", iconId: 56);
        Add(development, "开发文档", "docs@example.com", "https://docs.example.com", iconId: 61);

        var samples = new (KdbxGroup Group, int DirectCount, string Prefix)[]
        {
            (code, 5, "代码仓库"), (cloud, 12, "云服务"), (development, 15, "运维工具"),
            (work, 36, "工作账户"), (personal, 42, "个人账户"), (finance, 18, "金融账户"),
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
                    SetDemoTotp(sample, "KeePassModern Demo", "KRSXG5DSN5ZGK3TPOI");
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
        var vault = KdbxDatabase.CreateDefault("工作密码库");
        var archive = vault.RootGroup.Groups[0];
        archive.Name = "工作";
        var source = personalDemo.RootGroup.Groups.Single(group => group.Name == "工作");
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
