using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using KeePassModern.Core.Crypto;
using KeePassModern.Core.Kdbx;
using KeePassModern.Core.Services;
using KeePassModern.Helpers;

namespace KeePassModern.Views;

/// <summary>
/// 新建 / 编辑密码条目弹窗（严格按审核通过的方案 A 实现：零选项卡 · Fluent 2 单页卡片 + 内联添加 + Expander 折叠高级区 + 独立历史视图）。
/// </summary>
public partial class EntryEditorWindow : Window
{
    private readonly KdbxEntry? _editingEntry;
    private readonly KdbxGroup _targetGroup;
    private readonly KdbxDatabase? _database;
    private readonly Dictionary<string, ProtectedBinary> _pendingBinaries = new(StringComparer.Ordinal);
    private bool _passwordVisible;
    private bool _initializing = true;

    public string? SavedPasswordForClipboard { get; private set; }

    public EntryEditorWindow() : this(null, new KdbxGroup(), null) { }

    public EntryEditorWindow(KdbxEntry? entry, KdbxGroup targetGroup, KdbxDatabase? db)
    {
        InitializeComponent();
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 44);

        _editingEntry = entry;
        _targetGroup = targetGroup;
        _database = db;

        TargetGroupLabel.Text = string.IsNullOrWhiteSpace(targetGroup.Name) ? "根目录" : targetGroup.Name;
        ExpiryPicker.SelectedDate = DateTime.Now.AddYears(1);
        FavoriteToggle.IsCheckedChanged += (_, _) =>
            FavoriteToggleText.Text = FavoriteToggle.IsChecked == true ? "已收藏" : "收藏";

        if (entry is null)
        {
            Title = "添加新密码条目";
            DialogHeading.Text = "添加新密码条目";
            SaveButtonText.Text = "创建并保存";
            TemplateCombo.IsVisible = true;
            TemplateCombo.SelectedIndex = 0;
            HistoryToggleBtn.IsVisible = false;
            AutoCopyAfterSaveCheck.IsChecked = true;
        }
        else
        {
            Title = $"编辑条目 — {entry.Title}";
            DialogHeading.Text = $"编辑条目 — {entry.Title}";
            SaveButtonText.Text = "保存修改";
            TemplateCombo.IsVisible = false;
            HistoryToggleBtn.IsVisible = true;

            TitleBox.Text = entry.Title;
            UserNameBox.Text = entry.UserName;
            PasswordBox.Text = entry.Password;
            UrlBox.Text = entry.Url;
            NotesBox.Text = entry.Notes;
            FavoriteToggle.IsChecked = entry.IsFavorite;
            FavoriteToggleText.Text = entry.IsFavorite ? "已收藏" : "收藏";

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

            HistoryBtnText.Text = $"历史 ({entry.History.Count})";
            HistoryListBox.ItemsSource = entry.History
                .Select((h, i) => $"#{i + 1} · {h.LastModificationTime.LocalDateTime:yyyy-MM-dd HH:mm} · {h.Title} ({h.UserName})")
                .ToList();
        }

        _initializing = false;
        UpdateTitleInitials();
        UpdatePasswordStrength();
        UpdateTotpPreview();
        RefreshExpanderSummaries();
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
                TitleBox.Text = "银行卡 — ";
                TagsBox.Text = "金融; 银行卡";
                CustomFieldsBox.Text = "卡号=\n有效期=\nCVV=*";
                CustomFieldsSection.IsVisible = true;
                break;
            case "ApiKey":
                TitleBox.Text = "API 密钥 — ";
                TagsBox.Text = "开发; 生产";
                CustomFieldsBox.Text = "AccessKeyId=\nSecretAccessKey=*\nEndpoint=";
                CustomFieldsSection.IsVisible = true;
                break;
            case "Note":
                TitleBox.Text = "安全笔记 — ";
                TagsBox.Text = "笔记";
                QualityCheckBox.IsChecked = false;
                break;
        }
        UpdateTitleInitials();
        RefreshExpanderSummaries();
    }

    private void ToggleHistoryPanel_Click(object? sender, RoutedEventArgs e)
    {
        bool show = !HistoryOverlayPanel.IsVisible;
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
    private void UpdateExpanderSummaries(object? sender, RoutedEventArgs e) => RefreshExpanderSummaries();
    private void ExpiryPicker_Changed(object? sender, SelectionChangedEventArgs e) => RefreshExpanderSummaries();

    private void UpdateTitleInitials()
    {
        var t = TitleBox.Text?.Trim() ?? "";
        IconBadgeText.Text = string.IsNullOrEmpty(t) ? "KP" : (t.Length >= 2 ? t[..2].ToUpperInvariant() : t.ToUpperInvariant());
    }

    private void UpdatePasswordStrength()
    {
        var pwd = PasswordBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(pwd))
        {
            StrengthBar.Value = 0;
            StrengthText.Text = "密码强度：待输入";
            return;
        }
        var res = PasswordGenerator.EvaluateStrength(pwd);
        StrengthBar.Value = Math.Clamp(res.EntropyBits / 120.0, 0.05, 1.0);
        StrengthText.Text = $"{res.Label} · {res.EntropyBits:0} bits 熵值";
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
            ? $"{ExpiryPicker.SelectedDate.Value:yyyy-MM-dd} 到期"
            : "永不过期";
        var tags = (TagsBox.Text ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tagPart = tags.Length > 0 ? string.Join("  ", tags.Select(t => $"#{t}")) : "暂未添加标签";
        ExpanderTagsSummary.Text = $"{expPart}  ·  {tagPart}";

        var seq = string.IsNullOrWhiteSpace(AutoTypeSeqBox.Text) ? "继承分组默认按键序列" : AutoTypeSeqBox.Text.Trim();
        var qc = QualityCheckBox.IsChecked != false ? "已启用密码质量审计" : "已禁用单条质量审计 (KDBX 4.1)";
        ExpanderAdvancedSummary.Text = $"{seq}  ·  {qc}";
    }

    private void TogglePasswordVisibility_Click(object? sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        PasswordBox.PasswordChar = _passwordVisible ? '\0' : '•';
    }

    private async void GeneratePassword_Click(object? sender, RoutedEventArgs e)
    {
        var gen = new PasswordGeneratorWindow();
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

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
