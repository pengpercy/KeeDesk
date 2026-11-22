using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using KeeDesk.Core.Crypto;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using KeeDesk.Helpers;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

/// <summary>
/// 新建 / 编辑密码条目弹窗（严格按审核通过的方案 A 实现：零选项卡 · Fluent 2 单页卡片 + 内联添加 + Expander 折叠高级区 + 独立历史视图）。
/// </summary>
public partial class EntryEditorWindow : Window
{
    private readonly KdbxEntry? _editingEntry;
    private readonly KdbxGroup _targetGroup;
    private readonly KdbxDatabase? _database;
    private readonly Dictionary<string, ProtectedBinary> _pendingBinaries = new(StringComparer.Ordinal);
    private int _selectedIconId;
    private Guid _selectedCustomIconUuid;
    private bool _passwordVisible;
    private bool _initializing = true;

    public string? SavedPasswordForClipboard { get; private set; }

    public EntryEditorWindow() : this(null, new KdbxGroup(), null) { }

    public EntryEditorWindow(KdbxEntry? entry, KdbxGroup targetGroup, KdbxDatabase? db, bool showHistory = false)
    {
        InitializeComponent();
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 44);

        _editingEntry = entry;
        _targetGroup = targetGroup;
        _database = db;

        TargetGroupLabel.Text = string.IsNullOrWhiteSpace(targetGroup.Name) ? L("Str_Root") : targetGroup.Name;
        ExpiryPicker.SelectedDate = DateTime.Now.AddYears(1);
        FavoriteToggle.IsCheckedChanged += (_, _) =>
            FavoriteToggleText.Text = FavoriteToggle.IsChecked == true ? L("Str_Favorited") : L("Str_Favorite");

        if (entry is null)
        {
            Title = L("Str_New");
            DialogHeading.Text = L("Str_New");
            SaveButtonText.Text = L("Str_CreateEntry");
            TemplateCombo.IsVisible = true;
            TemplateCombo.SelectedIndex = 0;
            HistoryToggleBtn.IsVisible = false;
            AutoCopyAfterSaveCheck.IsChecked = false;
        }
        else
        {
            _selectedIconId = entry.IconId;
            _selectedCustomIconUuid = entry.CustomIconUuid;
            Title = L("Str_EditEntryFormat", entry.Title);
            RefreshIconBadgePreview();
            DialogHeading.Text = L("Str_EditEntry2");
            SaveButtonText.Text = L("Str_SaveChanges");
            TemplateCombo.IsVisible = false;
            HistoryToggleBtn.IsVisible = true;
            SetHistoryPanelVisible(showHistory);

            TitleBox.Text = entry.Title;
            UserNameBox.Text = entry.UserName;
            PasswordBox.Text = entry.Password;
            UrlBox.Text = entry.Url;
            NotesBox.Text = entry.Notes;
            FavoriteToggle.IsChecked = entry.IsFavorite;
            FavoriteToggleText.Text = entry.IsFavorite ? L("Str_Favorited") : L("Str_Favorite");

            TagsBox.Text = string.Join("; ", entry.Tags.Where(t => !string.Equals(t, "Favorite", StringComparison.OrdinalIgnoreCase)));
            TotpBox.Text = !string.IsNullOrEmpty(entry.GetString(KdbxConstants.FieldOtp))
                ? entry.GetString(KdbxConstants.FieldOtp)
                : entry.GetString(KdbxConstants.FieldTimeOtpSecretBase32);
            ExpiresCheck.IsChecked = entry.Expires;
            if (entry.Expires) ExpiryPicker.SelectedDate = entry.ExpiryTime.LocalDateTime;
            QualityCheckBox.IsChecked = entry.QualityCheck;
            OverrideUrlBox.Text = entry.OverrideUrl;

            AutoTypeEnabledCheck.IsChecked = entry.AutoType.Enabled;
            AutoTypeSeqBox.Text = entry.AutoType.DefaultSequence;
            AutoTypeObfuscateCheck.IsChecked = entry.AutoType.DataTransferObfuscation != 0;

            var sb = new StringBuilder();
            foreach (var kv in entry.GetCustomStrings())
            {
                if (kv.Key is KdbxConstants.FieldOtp or KdbxConstants.FieldTimeOtpSecretBase32) continue;
                var prefix = kv.Value.IsProtected ? "*" : "";
                sb.AppendLine($"{kv.Key}={prefix}{kv.Value.ReadString()}");
            }
            if (sb.Length > 0)
            {
                CustomFieldsBox.Text = sb.ToString();
                CustomFieldsSection.IsVisible = true;
            }

            foreach (var kv in entry.Binaries)
                _pendingBinaries[kv.Key] = kv.Value.Clone();
            RefreshAttachmentList();

            HistoryBtnText.Text = L("Str_HistoryFormat", entry.History.Count);
            HistoryListBox.ItemsSource = entry.History
                .Select((h, i) => $"#{i + 1} · {h.LastModificationTime.LocalDateTime:yyyy-MM-dd HH:mm} · {h.Title} ({h.UserName})")
                .ToList();
        }

        _initializing = false;
        UpdateTitleInitials();
        UpdatePasswordStrength();
        UpdateTotpPreview();
        RefreshExpanderSummaries();
        RefreshNotesSummary();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or ComboBox or TextBox) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }

    private void TemplateCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TemplateCombo.SelectedItem is not ComboBoxItem item) return;
        switch (item.Tag?.ToString())
        {
            case "Bank":
                TitleBox.Text = L("Str_BankCard");
                TagsBox.Text = L("Str_FinanceBankCard");
                CustomFieldsBox.Text = L("Str_CardNumberNExpirationDateNCVV");
                CustomFieldsSection.IsVisible = true;
                break;
            case "ApiKey":
                TitleBox.Text = L("Str_APIKey");
                TagsBox.Text = L("Str_DevelopmentProduction");
                CustomFieldsBox.Text = "AccessKeyId=\nSecretAccessKey=*\nEndpoint=";
                CustomFieldsSection.IsVisible = true;
                break;
            case "Note":
                TitleBox.Text = L("Str_SecureNote");
                TagsBox.Text = L("Str_Note");
                QualityCheckBox.IsChecked = false;
                break;
        }
        UpdateTitleInitials();
        RefreshExpanderSummaries();
    }

    private void ToggleHistoryPanel_Click(object? sender, RoutedEventArgs e)
        => SetHistoryPanelVisible(!HistoryOverlayPanel.IsVisible);

    private void SetHistoryPanelVisible(bool show)
    {
        HistoryOverlayPanel.IsVisible = show;
        MainFormScroll.IsVisible = !show;
    }

    private void ShowCustomFields_Click(object? sender, RoutedEventArgs e)
    {
        CustomFieldsSection.IsVisible = true;
        CustomFieldsBox.Focus();
    }

    private void TitleBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateTitleInitials();
    private void PasswordBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdatePasswordStrength();
    private void TotpBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateTotpPreview();
    private void NotesBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshNotesSummary();

    private void RefreshNotesSummary()
    {
        if (NotesSummary is null || NotesBox is null) return;
        var summary = new List<string>();
        if (!string.IsNullOrWhiteSpace(NotesBox.Text)) summary.Add(L("Str_HasNotes"));
        if (_pendingBinaries.Count > 0) summary.Add(L("Str_AttachmentsFormat", _pendingBinaries.Count));
        NotesSummary.Text = summary.Count > 0 ? string.Join(" · ", summary) : L("Str_AddNotesFieldsOrAttachments");
    }

    private void UpdateExpanderSummaries(object? sender, RoutedEventArgs e) => RefreshExpanderSummaries();
    private void ExpiryPicker_Changed(object? sender, SelectionChangedEventArgs e) => RefreshExpanderSummaries();

    private void UpdateTitleInitials()
    {
        var t = TitleBox.Text?.Trim() ?? "";
        if (SaveButton is not null) SaveButton.IsEnabled = _editingEntry is not null || t.Length > 0;
        IconBadgeText.Text = string.IsNullOrEmpty(t) ? "KP" : (t.Length >= 2 ? t[..2].ToUpperInvariant() : t.ToUpperInvariant());
    }

    private void UpdatePasswordStrength()
    {
        var pwd = PasswordBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(pwd))
        {
            StrengthBar.Value = 0;
            StrengthText.Text = L("Str_PasswordStrengthWaitingForInput");
            return;
        }
        var res = PasswordGenerator.EvaluateStrength(pwd);
        StrengthBar.Value = Math.Clamp(res.EntropyBits / 120.0, 0.05, 1.0);
        StrengthText.Text = FormatPasswordStrength(res);
    }

    private void UpdateTotpPreview()
    {
        var raw = TotpBox.Text?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            TotpPreviewBadge.IsVisible = false;
            return;
        }
        var settings = TotpGenerator.TryParseOtpAuthUri(raw);
        if (settings is null)
        {
            TotpPreviewBadge.IsVisible = false;
            return;
        }
        TotpPreviewText.Text = TotpGenerator.Generate(settings).FormattedCode;
        TotpPreviewBadge.IsVisible = true;
    }

    private void RefreshExpanderSummaries()
    {
        if (ExpanderTagsSummary is null) return;
        var expPart = ExpiresCheck.IsChecked == true && ExpiryPicker.SelectedDate.HasValue
            ? L("Str_ExpiresFormat", ExpiryPicker.SelectedDate.Value)
            : L("Str_NeverExpires");
        var tags = (TagsBox.Text ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tagPart = tags.Length > 0 ? string.Join("  ", tags.Select(t => $"#{t}")) : L("Str_NoTags2");
        ExpanderTagsSummary.Text = $"{expPart}  ·  {tagPart}";

        var seq = string.IsNullOrWhiteSpace(AutoTypeSeqBox.Text) ? L("Str_InheritTheGroupSDefaultKeySequence") : AutoTypeSeqBox.Text.Trim();
        var qc = QualityCheckBox.IsChecked != false ? L("Str_PasswordQualityChecksEnabled") : L("Str_PasswordQualityChecksDisabledForThisEntry");
        ExpanderAdvancedSummary.Text = $"{seq}  ·  {qc}";
    }

    private void TogglePasswordVisibility_Click(object? sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        PasswordBox.PasswordChar = _passwordVisible ? '\0' : '•';
    }

    private async void GeneratePassword_Click(object? sender, RoutedEventArgs e)
    {
        var gen = new PasswordGeneratorWindow(fillEntry: true);
        var pwd = await gen.ShowDialog<string?>(this);
        if (!string.IsNullOrEmpty(pwd))
            PasswordBox.Text = pwd;
    }

    private async void AddAttachment_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        var file = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;

        var bytes = await File.ReadAllBytesAsync(file);
        var name = Path.GetFileName(file);
        _pendingBinaries[name] = new ProtectedBinary(true, bytes);
        RefreshAttachmentList();
    }

    private void RemoveAttachment_Click(object? sender, RoutedEventArgs e)
    {
        if (AttachmentsListBox.SelectedIndex < 0) return;
        var key = _pendingBinaries.Keys.ElementAtOrDefault(AttachmentsListBox.SelectedIndex);
        if (key is not null)
        {
            _pendingBinaries[key].Dispose();
            _pendingBinaries.Remove(key);
            RefreshAttachmentList();
        }
    }

    private void RefreshAttachmentList()
    {
        RefreshNotesSummary();
        AttachmentsSection.IsVisible = _pendingBinaries.Count > 0;
        AttachmentsListBox.ItemsSource = _pendingBinaries
            .Select(kv => $"{kv.Key}  ({kv.Value.Length / 1024.0:0.0} KB)")
            .ToList();
    }

    private void ExpiryPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int days))
        {
            ExpiresCheck.IsChecked = true;
            ExpiryPicker.SelectedDate = DateTime.Now.AddDays(days);
            RefreshExpanderSummaries();
        }
    }

    private void RestoreHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingEntry is null || HistoryListBox.SelectedIndex < 0 || HistoryListBox.SelectedIndex >= _editingEntry.History.Count)
            return;
        var snap = _editingEntry.History[HistoryListBox.SelectedIndex];
        _editingEntry.RestoreFromHistory(snap, _database?.Meta.HistoryMaxItems ?? 10);
        Close(true);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var entry = _editingEntry ?? new KdbxEntry();
        if (_editingEntry is not null)
            entry.CreateHistorySnapshot(_database?.Meta.HistoryMaxItems ?? 10);

        entry.Title = TitleBox.Text?.Trim() ?? string.Empty;
        entry.IconId = _selectedIconId;
        entry.CustomIconUuid = _selectedCustomIconUuid;
        entry.UserName = UserNameBox.Text ?? string.Empty;
        entry.Password = PasswordBox.Text ?? string.Empty;
        entry.Url = UrlBox.Text?.Trim() ?? string.Empty;
        entry.Notes = NotesBox.Text ?? string.Empty;
        entry.QualityCheck = QualityCheckBox.IsChecked != false;
        entry.OverrideUrl = OverrideUrlBox.Text?.Trim() ?? string.Empty;

        entry.Tags = (TagsBox.Text ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        entry.IsFavorite = FavoriteToggle.IsChecked == true;

        var totp = TotpBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(totp))
            entry.SetString(KdbxConstants.FieldOtp, totp, true);
        else
            entry.Strings.Remove(KdbxConstants.FieldOtp);

        entry.Expires = ExpiresCheck.IsChecked == true;
        if (entry.Expires && ExpiryPicker.SelectedDate.HasValue)
            entry.ExpiryTime = new DateTimeOffset(ExpiryPicker.SelectedDate.Value);

        entry.AutoType.Enabled = AutoTypeEnabledCheck.IsChecked != false;
        entry.AutoType.DefaultSequence = AutoTypeSeqBox.Text?.Trim() ?? string.Empty;
        entry.AutoType.DataTransferObfuscation = AutoTypeObfuscateCheck.IsChecked == true ? 1 : 0;

        // 同步自定义字段
        var customLines = (CustomFieldsBox.Text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in customLines)
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var k = line[..idx].Trim();
            var v = line[(idx + 1)..].TrimEnd('\r');
            bool isProt = v.StartsWith('*');
            if (isProt) v = v[1..];
            if (!string.IsNullOrEmpty(k))
                entry.SetString(k, v, isProt);
        }

        // 同步附件
        foreach (var pb in entry.Binaries.Values) pb.Dispose();
        entry.Binaries.Clear();
        foreach (var kv in _pendingBinaries)
            entry.Binaries[kv.Key] = kv.Value;

        entry.LastModificationTime = DateTimeOffset.UtcNow;
        if (_editingEntry is null)
            _targetGroup.AddEntry(entry);

        if (AutoCopyAfterSaveCheck.IsChecked == true && !string.IsNullOrEmpty(entry.Password))
            SavedPasswordForClipboard = entry.Password;

        Close(true);
    }

    private void RefreshIconBadgePreview()
    {
        var bmp = KeeDesk.Services.FaviconFetcherService.TryDecodeCustomIcon(_database, _selectedCustomIconUuid);
        if (bmp is not null)
        {
            IconBadgeImage.Source = bmp;
            IconBadgeImage.IsVisible = true;
            IconBadgeText.IsVisible = false;
        }
        else
        {
            IconBadgeImage.Source = null;
            IconBadgeImage.IsVisible = false;
            IconBadgeText.IsVisible = true;
        }
    }

    private async void AutoFetchFavicon_Click(object? sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        var domain = KeeDesk.Services.FaviconFetcherService.ExtractDomain(UrlBox.Text, UserNameBox.Text);
        if (string.IsNullOrEmpty(domain))
        {
            StrengthText.Text = L("Str_EnterAWebsiteURLOrAnEmail");
            return;
        }

        StrengthText.Text = L("Str_FetchingWebsiteIconFromFormat", domain);
        var bytes = await KeeDesk.Services.FaviconFetcherService.FetchByDomainAsync(domain);
        if (bytes is null)
        {
            StrengthText.Text = L("Str_CouldnTFetchAnIconFromChooseFormat", domain);
            return;
        }

        _selectedCustomIconUuid = KeeDesk.Services.FaviconFetcherService.AddOrReuseCustomIcon(_database, domain, bytes);
        RefreshIconBadgePreview();
        StrengthText.Text = L("Str_FetchedWebsiteIconForFormat", domain);
    }

    private void ResetLetterIcon_Click(object? sender, RoutedEventArgs e)
    {
        _selectedCustomIconUuid = Guid.Empty;
        RefreshIconBadgePreview();
    }

    private async void PickIcon_Click(object? sender, RoutedEventArgs e)
    {
        var suggestedDomain = KeeDesk.Services.FaviconFetcherService.ExtractDomain(UrlBox.Text, UserNameBox.Text) ?? string.Empty;
        var dlg = new IconPickerWindow(_database, _selectedIconId, _selectedCustomIconUuid, suggestedDomain);
        var res = await dlg.ShowDialog<IconPickerResult?>(this);
        if (res is null) return;
        _selectedIconId = res.IconId;
        _selectedCustomIconUuid = res.CustomIconUuid;
        RefreshIconBadgePreview();
    }

    private async void InsertUserNameRef_Click(object? sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        var dlg = new FieldReferenceWindow(_database, 'U');
        var expr = await dlg.ShowDialog<string?>(this);
        if (!string.IsNullOrEmpty(expr))
            UserNameBox.Text = expr;
    }

    private async void InsertPasswordRef_Click(object? sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        var dlg = new FieldReferenceWindow(_database, 'P');
        var expr = await dlg.ShowDialog<string?>(this);
        if (!string.IsNullOrEmpty(expr))
            PasswordBox.Text = expr;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
