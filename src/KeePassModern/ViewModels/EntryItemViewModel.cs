using CommunityToolkit.Mvvm.ComponentModel;
using KeePassModern.Core.Kdbx;
using KeePassModern.Core.Services;

namespace KeePassModern.ViewModels;

public sealed partial class EntryItemViewModel : ViewModelBase
{
    public KdbxEntry Model { get; }

    public EntryItemViewModel(KdbxEntry entry)
    {
        Model = entry;
        Refresh();
    }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _maskedPassword = "••••••••••••••••";
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _initials = "KP";
    [ObservableProperty] private string _groupPath = string.Empty;
    [ObservableProperty] private string _tagsDisplay = string.Empty;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isPasswordRevealed;
    [ObservableProperty] private bool _hasTotp;
    [ObservableProperty] private string _totpFormatted = string.Empty;
    [ObservableProperty] private string _totpRaw = string.Empty;
    [ObservableProperty] private int _totpRemaining;
    [ObservableProperty] private double _totpProgress;
    [ObservableProperty] private string _strengthLabel = string.Empty;
    [ObservableProperty] private double _strengthRatio;
    [ObservableProperty] private string _expiryBadge = string.Empty;
    [ObservableProperty] private bool _hasExpiryBadge;
    [ObservableProperty] private int _historyCount;
    [ObservableProperty] private int _attachmentCount;
    [ObservableProperty] private string _footerMeta = string.Empty;

    public string DisplayedPassword => IsPasswordRevealed ? Password : MaskedPassword;

    public IReadOnlyList<KeyValuePair<string, string>> CustomFields
        => Model.GetCustomStrings()
            .Where(kv => kv.Key is not KdbxConstants.FieldOtp and not KdbxConstants.FieldTimeOtpSecretBase32)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.IsProtected ? "••••••••" : kv.Value.ReadString()))
            .ToList();

    public IReadOnlyList<string> AttachmentNames
        => Model.Binaries.Select(kv => $"{kv.Key} ({FormatBytes(kv.Value.Length)})").ToList();

    partial void OnIsPasswordRevealedChanged(bool value) => OnPropertyChanged(nameof(DisplayedPassword));

    public void Refresh()
    {
        Title = string.IsNullOrWhiteSpace(Model.Title) ? "未命名条目" : Model.Title;
        UserName = Model.UserName;
        Password = Model.Password;
        MaskedPassword = string.IsNullOrEmpty(Password) ? "（空密码）" : new string('•', Math.Clamp(Password.Length, 8, 22));
        Url = Model.Url;
        Notes = Model.Notes;
        IsFavorite = Model.IsFavorite;
        GroupPath = Model.Parent?.GetFullPath("  /  ") ?? string.Empty;
        TagsDisplay = Model.Tags.Count > 0 ? string.Join("   ", Model.Tags.Select(t => $"#{t}")) : "无标签";

        var words = Title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Initials = words.Length >= 2
            ? $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}"
            : Title.Length >= 2 ? Title[..2].ToUpperInvariant() : Title.ToUpperInvariant();

        var strength = PasswordGenerator.EvaluateStrength(Password);
        StrengthLabel = $"{strength.Label} · {strength.EntropyBits:0} bits 熵值";
        StrengthRatio = Math.Clamp(strength.EntropyBits / 120.0, 0.05, 1.0);

        if (Model.Expires)
        {
            var days = (int)Math.Ceiling((Model.ExpiryTime - DateTimeOffset.UtcNow).TotalDays);
            HasExpiryBadge = days <= 14;
            ExpiryBadge = days <= 0 ? "已过期" : $"{days} 天后过期";
        }
        else
        {
            HasExpiryBadge = false;
            ExpiryBadge = string.Empty;
        }

        HistoryCount = Model.History.Count;
        AttachmentCount = Model.Binaries.Count;
        FooterMeta = $"修改时间：{Model.LastModificationTime.LocalDateTime:yyyy-MM-dd HH:mm}   ·   创建时间：{Model.CreationTime.LocalDateTime:yyyy-MM-dd HH:mm}   ·   UUID: {Model.Uuid.ToString("N")[..8]}...";

        RefreshTotp();
        OnPropertyChanged(nameof(DisplayedPassword));
        OnPropertyChanged(nameof(CustomFields));
        OnPropertyChanged(nameof(AttachmentNames));
    }

    public void RefreshTotp()
    {
        var settings = TotpGenerator.TryExtractFromEntry(Model);
        if (settings is null)
        {
            HasTotp = false;
            TotpFormatted = string.Empty;
            TotpRaw = string.Empty;
            return;
        }

        var res = TotpGenerator.Generate(settings);
        HasTotp = true;
        TotpFormatted = res.FormattedCode;
        TotpRaw = res.Code;
        TotpRemaining = res.RemainingSeconds;
        TotpProgress = (double)res.RemainingSeconds / Math.Max(1, res.Period);
    }

    private static string FormatBytes(int len)
        => len < 1024 ? $"{len} B" : $"{len / 1024.0:0.0} KB";
}
