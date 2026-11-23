using Avalonia.Media;
using Avalonia.Media.Imaging;
using KeeDesk.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using KeeDesk.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.ViewModels;

public sealed record CustomFieldItem(string Key, string DisplayValue, string RawValue, bool IsProtected);
public sealed record AttachmentItem(string FileName, string DisplaySize, string DisplayLabel);

public sealed partial class EntryItemViewModel : ViewModelBase
{
    public KdbxEntry Model { get; }
    private readonly KdbxDatabase? _database;
    private DateTimeOffset _totpExpiresAt;
    private int _totpPeriod;
    private PasswordStrengthResult _strength = null!;

    public EntryItemViewModel(KdbxEntry entry, KdbxDatabase? database = null)
    {
        Model = entry;
        _database = database;
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
    [ObservableProperty] private string _strengthLabel = string.Empty;
    [ObservableProperty] private double _strengthRatio;
    [ObservableProperty] private string _expiryBadge = string.Empty;
    [ObservableProperty] private bool _hasExpiryBadge;
    [ObservableProperty] private int _historyCount;
    [ObservableProperty] private int _attachmentCount;
    [ObservableProperty] private string _footerMeta = string.Empty;
    [ObservableProperty] private Bitmap? _customIconBitmap;
    public bool HasCustomIcon => CustomIconBitmap is not null;
    public bool HasStandardIcon => !HasCustomIcon && StandardIcon is not null;
    public bool ShowInitials => !HasCustomIcon && !HasStandardIcon;
    public Geometry? StandardIcon { get; private set; }
    public string EntryKind => string.IsNullOrWhiteSpace(Url) ? L("Str_PasswordEntry") : L("Str_WebsiteLogin");
    public string UpdatedDisplay => L("Str_UpdatedFormat", Model.LastModificationTime.LocalDateTime);
    public IEnumerable<string> DisplayTags => Model.Tags.Where(t =>
        !string.Equals(t, "Favorite", StringComparison.OrdinalIgnoreCase) && t != "收藏");
    public bool HasAnnotations => !string.IsNullOrWhiteSpace(Notes) || Model.Binaries.Count > 0 || DisplayTags.Any()
        || Model.GetCustomStrings().Any(kv => kv.Key is not KdbxConstants.FieldOtp and not KdbxConstants.FieldTimeOtpSecretBase32);

    public string DisplayedPassword => IsPasswordRevealed ? Password : MaskedPassword;

    public IReadOnlyList<CustomFieldItem> CustomFields
        => Model.GetCustomStrings()
            .Where(kv => kv.Key is not KdbxConstants.FieldOtp and not KdbxConstants.FieldTimeOtpSecretBase32)
            .Select(kv =>
            {
                var raw = kv.Value.ReadString();
                return new CustomFieldItem(kv.Key, kv.Value.IsProtected ? "••••••••" : raw, raw, kv.Value.IsProtected);
            })
            .ToList();

    public IReadOnlyList<AttachmentItem> Attachments
        => Model.Binaries
            .Select(kv =>
            {
                var size = FormatBytes(kv.Value.Length);
                return new AttachmentItem(kv.Key, size, $"{kv.Key} ({size})");
            })
            .ToList();

    partial void OnIsPasswordRevealedChanged(bool value) => OnPropertyChanged(nameof(DisplayedPassword));

    public void Refresh()
    {
        UserName = Model.UserName;
        Password = Model.Password;
        Url = Model.Url;
        Notes = Model.Notes;
        IsFavorite = Model.IsFavorite;
        const string separator = "  /  ";
        var fullPath = Model.Parent?.GetFullPath(separator) ?? string.Empty;
        var rootPrefix = _database?.RootGroup.Name + separator;
        GroupPath = _database is not null && fullPath.StartsWith(rootPrefix, StringComparison.Ordinal)
            ? fullPath[rootPrefix.Length..]
            : fullPath;
        _strength = PasswordGenerator.EvaluateStrength(Password);
        StrengthRatio = Math.Clamp(_strength.EntropyBits / 120.0, 0.05, 1.0);

        HistoryCount = Model.History.Count;
        AttachmentCount = Model.Binaries.Count;
        CustomIconBitmap = FaviconFetcherService.TryDecodeCustomIcon(_database, Model.CustomIconUuid);
        StandardIcon = Model.IconId != 0 ? KeePassIconCatalog.GetGeometry(Model.IconId) : null;
        OnPropertyChanged(nameof(HasCustomIcon));
        OnPropertyChanged(nameof(HasStandardIcon));
        OnPropertyChanged(nameof(ShowInitials));
        OnPropertyChanged(nameof(StandardIcon));

        RefreshTotp();
        RefreshLanguage();
        OnPropertyChanged(nameof(DisplayTags));
        OnPropertyChanged(nameof(CustomFields));
        OnPropertyChanged(nameof(HasAnnotations));
    }

    internal void RefreshLanguage()
    {
        var title = Model.Title;
        Title = string.IsNullOrWhiteSpace(title) ? L("Str_UntitledEntry") : title;
        var words = Title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Initials = words.Length >= 2
            ? $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}"
            : Title.Length >= 2 ? Title[..2].ToUpperInvariant() : Title.ToUpperInvariant();

        bool emptyPassword = string.IsNullOrEmpty(Password);
        MaskedPassword = emptyPassword ? L("Str_EmptyPassword") : new string('•', Math.Clamp(Password.Length, 8, 22));
        StrengthLabel = FormatPasswordStrength(_strength, emptyPassword);
        TagsDisplay = Model.Tags.Count > 0 ? string.Join("   ", Model.Tags.Select(t => $"#{t}")) : L("Str_NoTags");

        if (Model.Expires)
        {
            var days = (int)Math.Ceiling((Model.ExpiryTime - DateTimeOffset.UtcNow).TotalDays);
            HasExpiryBadge = days <= 14;
            ExpiryBadge = days <= 0 ? L("Str_Expired") : L("Str_ExpiresInDaysFormat", days);
        }
        else
        {
            HasExpiryBadge = false;
            ExpiryBadge = string.Empty;
        }

        FooterMeta = L("Str_ModifiedCreatedUUIDFormat", Model.LastModificationTime.LocalDateTime, Model.CreationTime.LocalDateTime, Model.Uuid.ToString("N")[..8]);

        OnPropertyChanged(nameof(DisplayedPassword));
        OnPropertyChanged(nameof(EntryKind));
        OnPropertyChanged(nameof(UpdatedDisplay));
        OnPropertyChanged(nameof(Attachments));
    }

    public void RefreshTotp(DateTimeOffset? timestamp = null)
    {
        var settings = TotpGenerator.TryExtractFromEntry(Model);
        if (settings is null)
        {
            HasTotp = false;
            TotpFormatted = string.Empty;
            TotpRaw = string.Empty;
            TotpRemaining = 0;
            return;
        }

        var now = timestamp ?? DateTimeOffset.UtcNow;
        var res = TotpGenerator.Generate(settings, now);
        _totpPeriod = res.Period;
        _totpExpiresAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() + res.RemainingSeconds);
        TotpFormatted = res.FormattedCode;
        TotpRaw = res.Code;
        TotpRemaining = res.RemainingSeconds;
        HasTotp = true;
    }

    internal double GetTotpProgress(DateTimeOffset now)
    {
        if (!HasTotp) return 0;

        var remaining = (_totpExpiresAt - now).TotalSeconds;
        if (remaining <= 0 || remaining > _totpPeriod)
        {
            RefreshTotp(now);
            if (!HasTotp) return 0;
            remaining = (_totpExpiresAt - now).TotalSeconds;
        }

        TotpRemaining = (int)Math.Ceiling(remaining);
        return remaining / _totpPeriod;
    }

    private static string FormatBytes(int len)
        => len < 1024 ? $"{len} B" : $"{len / 1024.0:0.0} KB";
}
