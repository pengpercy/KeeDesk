using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeePassModern.Core.Kdbx;
using KeePassModern.Core.Services;
using KeePassModern.Helpers;
using KeePassModern.Services;

namespace KeePassModern.ViewModels;

public enum ActiveSection
{
    AllEntries,
    Favorites,
    TotpOnly,
    SecurityAudit,
    RecycleBin,
    Group,
    Tag,
    CustomFilter
}

/// <summary>
/// 表示一个已解锁的密码库标签页会话，支持同时打开多个 .kdbx 自由切换。
/// </summary>
public sealed partial class VaultTabSession : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public KdbxDatabase Database { get; }
    public CompositeKey MasterKey { get; set; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private bool _isActive;

    public ActiveSection SavedSection { get; set; } = ActiveSection.AllEntries;
    public Guid? SavedGroupUuid { get; set; }
    public Guid? SavedEntryUuid { get; set; }

    public VaultTabSession(KdbxDatabase database, CompositeKey masterKey, string displayName)
    {
        Database = database;
        MasterKey = masterKey;
        _displayName = displayName;
    }
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public AppSettingsService SettingsService { get; }
    private KdbxDatabase? _database;
    private CompositeKey? _masterKey;
    private VaultTabSession? _activeSession;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
    private readonly DispatcherTimer _tickTimer;
    private CancellationTokenSource? _clipboardClearCts;
    private string? _lastCopiedSecret;

    public ObservableCollection<VaultTabSession> OpenVaults { get; } = new();
    public bool HasMultipleVaults => OpenVaults.Count > 1;

    public bool IsMacOS { get; } = OperatingSystem.IsMacOS();
    public bool IsWindows { get; } = OperatingSystem.IsWindows();
    public bool IsWindowsOrLinux => !IsMacOS;
    public string SearchShortcutHint => IsMacOS ? "⌘K" : "Ctrl+K";
    public string LockShortcutHint => IsMacOS ? "⌘L" : "Ctrl+L";

    // 导航选中状态，用于驱动左侧 3px 品牌色指示条
    public bool IsAllEntriesSelected => ActiveSection == ActiveSection.AllEntries;
    public bool IsFavoritesSelected => ActiveSection == ActiveSection.Favorites;
    public bool IsTotpSelected => ActiveSection == ActiveSection.TotpOnly;
    public bool IsAuditSelected => ActiveSection == ActiveSection.SecurityAudit;
    public bool IsRecycleBinSelected => ActiveSection == ActiveSection.RecycleBin;

    [ObservableProperty] private string _currentDensityLabel = "标准";
    [ObservableProperty] private bool _sortByModifiedDesc;
    public string SortButtonLabel => SortByModifiedDesc ? "最近修改" : "名称排序";

    [ObservableProperty] private double _macTrafficLightsPadding = 76;
    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private bool _isUnlocking;
    [ObservableProperty] private bool _isAuditViewActive;

    // 供视图订阅的丝滑动画事件：解锁失败抖动、解锁成功转场、聚焦搜索框
    public event Action? UnlockFailedShakeRequested;
    public event Action? FocusSearchRequested;

    // Unlock / Welcome view properties
    [ObservableProperty] private string _unlockVaultPath = string.Empty;
    [ObservableProperty] private string _unlockKeyFilePath = string.Empty;
    [ObservableProperty] private string _unlockMasterPassword = string.Empty;
    [ObservableProperty] private bool _unlockUseWindowsUserAccount;
    [ObservableProperty] private string _unlockError = string.Empty;
    [ObservableProperty] private string _newVaultName = "个人密码库";
    [ObservableProperty] private RecentVaultItem? _selectedRecentVault;
    public ObservableCollection<RecentVaultItem> RecentVaults { get; } = new();

    public string UnlockVaultHeading => string.IsNullOrWhiteSpace(UnlockVaultPath)
        ? "欢迎使用 KeePassModern"
        : $"解锁「{Path.GetFileName(UnlockVaultPath)}」";

    public string UnlockKeyFileDisplay => string.IsNullOrWhiteSpace(UnlockKeyFilePath)
        ? "未选择密钥文件（点击浏览 .keyx）"
        : Path.GetFileName(UnlockKeyFilePath);

    // Workspace properties
    [ObservableProperty] private string _vaultDisplayName = "KeePassModern";
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _currentFilterTitle = "全部条目";
    [ObservableProperty] private ActiveSection _activeSection = ActiveSection.AllEntries;
    [ObservableProperty] private int _allCount;
    [ObservableProperty] private int _favoritesCount;
    [ObservableProperty] private int _totpCount;
    [ObservableProperty] private int _auditIssueCount;
    [ObservableProperty] private int _recycleBinCount;
    [ObservableProperty] private bool _isModified;

    // Collections
    public ObservableCollection<GroupItemViewModel> RootGroups { get; } = new();
    public ObservableCollection<string> AllTags { get; } = new();
    public ObservableCollection<EntryItemViewModel> FilteredEntries { get; } = new();
    private List<EntryItemViewModel> _selectedEntries = new();
    [ObservableProperty] private int _selectedEntriesCount;
    public bool HasMultipleSelectedEntries => SelectedEntriesCount > 1;
    [ObservableProperty] private EntryItemViewModel? _selectedEntry;
    [ObservableProperty] private GroupItemViewModel? _selectedGroup;
    private string? _selectedTag;
    private string _customFilterTitle = "查找结果";
    private List<KdbxEntry>? _customFilterEntries;

    // Security Audit Dashboard
    [ObservableProperty] private int _auditScore = 100;
    public ObservableCollection<EntryItemViewModel> AuditWeakEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditReusedEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditExpiringEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditNo2FaEntries { get; } = new();

    // Toast notification
    [ObservableProperty] private string _toastMessage = string.Empty;
    [ObservableProperty] private bool _isToastVisible;

    // 详情页复制按钮绿色对钩短暂反馈状态
    [ObservableProperty] private bool _isUserNameCopied;
    [ObservableProperty] private bool _isPasswordCopied;
    [ObservableProperty] private bool _isTotpCopied;

    // Callbacks wired by MainWindow for native dialogs/clipboard
    public Func<string, Task>? CopyToClipboardAsync { get; set; }
    public Func<Task<string?>>? GetClipboardTextAsync { get; set; }
    public Func<KdbxEntry?, KdbxGroup, Task<bool>>? OpenEntryEditorAsync { get; set; }
    public Func<Task<string?>>? OpenPasswordGeneratorAsync { get; set; }
    public Func<Task>? OpenSettingsAsync { get; set; }
    public Func<Task>? OpenAboutAsync { get; set; }
    public Func<ReleaseInfo, Task>? ShowUpdateWindowAsync { get; set; }
    public Func<string, Task<string?>>? PickOpenFileAsync { get; set; }
    public Func<string, string, Task<string?>>? PickSaveFileAsync { get; set; }
    public Func<string, string, string, Task<(string Name, string Notes)?>>? OpenGroupDialogAsync { get; set; }
    public Func<KdbxDatabase, Task<(bool Saved, CompositeKey? NewKey, string? NewKeyFile)>>? OpenDatabaseSettingsAsync { get; set; }
    public Func<string, string, string, Task<(KdbxDatabase Database, CompositeKey MasterKey, string KeyFilePath)?>>? OpenVaultUnlockPromptAsync { get; set; }
    public Func<KdbxDatabase, KdbxEntry, Task<KdbxGroup?>>? OpenMoveToGroupAsync { get; set; }
    public Func<KdbxDatabase, Task<bool>>? OpenDatabaseMaintenanceAsync { get; set; }
    public Func<Task<EntrySearchParameters?>>? OpenAdvancedSearchAsync { get; set; }
    public Func<string, List<AutoTypeMatchResult>, Task<AutoTypeMatchResult?>>? OpenAutoTypePickerAsync { get; set; }
    public Action<string>? OpenUrlExternal { get; set; }
    public Action? MinimizeWindowAction { get; set; }

    public bool HasRecycleBinItems => RecycleBinCount > 0;
    public bool HasUnlockKeyFile => !string.IsNullOrWhiteSpace(UnlockKeyFilePath);

    public MainWindowViewModel(AppSettingsService settingsService)
    {
        SettingsService = settingsService;
        CurrentDensityLabel = DensityService.Current switch
        {
            UiDensity.Compact => "紧凑",
            UiDensity.Comfortable => "宽松",
            _ => "标准"
        };
        RefreshRecentVaults();

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => OnSecondTick();
        _tickTimer.Start();
    }

    public KdbxDatabase? CurrentDatabase => _database;

    public void UpdateMacTitleBarInsets(double trafficLightsRight)
    {
        MacTrafficLightsPadding = Math.Max(68, trafficLightsRight + 12);
    }

    public void RecordUserActivity() => _lastActivityUtc = DateTimeOffset.UtcNow;

    private void OnSecondTick()
    {
        if (!IsUnlocked) return;

        // 空闲超时自动锁定（对标原版 KeePass WorkspaceLocking）
        int idleMins = SettingsService.Settings.AutoLockIdleMinutes;
        if (idleMins > 0 && (DateTimeOffset.UtcNow - _lastActivityUtc).TotalMinutes >= idleMins)
        {
            LockVault();
            ShowToast($"已连续 {idleMins} 分钟无操作，密码库已自动锁定");
            return;
        }

        foreach (var item in FilteredEntries)
        {
            if (item.HasTotp) item.RefreshTotp();
        }
        SelectedEntry?.RefreshTotp();
    }

    partial void OnUnlockVaultPathChanged(string value) => OnPropertyChanged(nameof(UnlockVaultHeading));
    partial void OnUnlockKeyFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(UnlockKeyFileDisplay));
        OnPropertyChanged(nameof(HasUnlockKeyFile));
    }
    partial void OnRecycleBinCountChanged(int value) => OnPropertyChanged(nameof(HasRecycleBinItems));
    partial void OnSelectedEntriesCountChanged(int value) => OnPropertyChanged(nameof(HasMultipleSelectedEntries));

    public void UpdateSelectedEntries(IEnumerable<EntryItemViewModel> items)
    {
        _selectedEntries = items.ToList();
        SelectedEntriesCount = _selectedEntries.Count;
    }

    partial void OnSelectedRecentVaultChanged(RecentVaultItem? value)
    {
        if (value is not null)
            SelectRecentVault(value);
    }

    private void RefreshRecentVaults()
    {
        RecentVaults.Clear();
        foreach (var v in SettingsService.Settings.RecentVaults)
            RecentVaults.Add(v);
        if (string.IsNullOrEmpty(UnlockVaultPath) && RecentVaults.Count > 0)
        {
            SelectedRecentVault = RecentVaults[0];
            UnlockVaultPath = RecentVaults[0].FilePath;
        }
    }

    [RelayCommand]
    private void SelectRecentVault(RecentVaultItem? item)
    {
        if (item is null) return;
        UnlockVaultPath = item.FilePath;
        UnlockKeyFilePath = item.KeyFilePath ?? string.Empty;
        UnlockError = string.Empty;
    }

    [RelayCommand]
    private void RemoveRecentVault(RecentVaultItem? item)
    {
        if (item is null) return;
        SettingsService.RemoveRecentVault(item.FilePath);
        if (string.Equals(UnlockVaultPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            UnlockVaultPath = string.Empty;
            UnlockKeyFilePath = string.Empty;
        }
        RefreshRecentVaults();
        ShowToast($"已从最近列表移除「{item.DisplayName}」（本地文件不受影响）");
    }

    [RelayCommand]
    private void ClearRecentVaults()
    {
        SettingsService.ClearRecentVaults();
        UnlockVaultPath = string.Empty;
        UnlockKeyFilePath = string.Empty;
        RefreshRecentVaults();
        ShowToast("已清空最近打开的密码库记录");
    }

    [RelayCommand]
    private async Task BrowseVaultFileAsync()
    {
        if (PickOpenFileAsync is null) return;
        var path = await PickOpenFileAsync("kdbx");
        if (!string.IsNullOrEmpty(path))
        {
            UnlockVaultPath = path;
            UnlockError = string.Empty;
        }
    }

    [RelayCommand]
    private async Task BrowseKeyFileAsync()
    {
        if (PickOpenFileAsync is null) return;
        var path = await PickOpenFileAsync("*");
        if (!string.IsNullOrEmpty(path))
            UnlockKeyFilePath = path;
    }

    [RelayCommand]
    private void ClearKeyFile()
    {
        UnlockKeyFilePath = string.Empty;
    }

    [RelayCommand]
    private async Task GenerateKeyFileAsync()
    {
        if (PickSaveFileAsync is null) return;
        var suggested = string.IsNullOrWhiteSpace(NewVaultName) ? "KeePassModern.keyx" : $"{NewVaultName}.keyx";
        var path = await PickSaveFileAsync(suggested, "keyx");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var xml = CompositeKey.GenerateXmlKeyFileV2();
            await File.WriteAllTextAsync(path, xml);
            if (!IsUnlocked)
                UnlockKeyFilePath = path;
            ShowToast($"已生成 XML v2.0 密钥文件：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            ShowToast($"生成密钥文件失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 处理拖放到主窗口的文件：支持直接拖入 .kdbx 密码库文件或 .keyx/.key 密钥文件。
    /// </summary>
    public void HandleDroppedFiles(IEnumerable<string> paths)
    {
        var list = paths.Where(File.Exists).ToList();
        if (list.Count == 0) return;

        var kdbx = list.FirstOrDefault(p => p.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(kdbx))
        {
            if (IsUnlocked)
                LockVault();
            UnlockVaultPath = kdbx;
            UnlockError = string.Empty;
            ShowToast($"已载入密码库：{Path.GetFileName(kdbx)}，请输入主密码解锁");
            return;
        }

        if (!IsUnlocked)
        {
            var keyFile = list.FirstOrDefault(p =>
                p.EndsWith(".keyx", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".key", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(keyFile))
            {
                UnlockKeyFilePath = keyFile;
                ShowToast($"已选择密钥文件：{Path.GetFileName(keyFile)}");
            }
        }
    }

    [RelayCommand]
    private async Task UnlockVaultAsync()
    {
        if (IsUnlocking) return;
        UnlockError = string.Empty;
        if (string.IsNullOrWhiteSpace(UnlockVaultPath) || !File.Exists(UnlockVaultPath))
        {
            UnlockError = "请选择有效的 .kdbx 密码库文件。";
            UnlockFailedShakeRequested?.Invoke();
            return;
        }

        IsUnlocking = true;
        var vaultPath = UnlockVaultPath;
        var pwd = UnlockMasterPassword;
        var keyFile = UnlockKeyFilePath;
        var useWua = UnlockUseWindowsUserAccount && IsWindows;

        try
        {
            // 后台线程执行 Argon2id / AES-KDF 解密，保持 60fps 动画完全流畅不卡顿
            var (db, key) = await Task.Run(() =>
            {
                var k = CompositeKey.FromPasswordAndKeyFile(pwd, keyFile, useWua);
                var loaded = KdbxFormat.Load(vaultPath, k);
                return (loaded, k);
            });

            ActivateDatabase(db, key);
            SettingsService.AddRecentVault(vaultPath, string.IsNullOrWhiteSpace(keyFile) ? null : keyFile, db.Meta.DatabaseName);
            RefreshRecentVaults();
            UnlockMasterPassword = string.Empty;
        }
        catch (Exception ex)
        {
            UnlockError = $"解锁失败：主密码或密钥文件不正确（{ex.Message}）";
            UnlockFailedShakeRequested?.Invoke();
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    [RelayCommand]
    private async Task CreateNewVaultAsync()
    {
        UnlockError = string.Empty;
        if (string.IsNullOrWhiteSpace(UnlockMasterPassword))
        {
            UnlockError = "请先在上方输入新密码库的主密码。";
            return;
        }

        string? savePath = PickSaveFileAsync is not null
            ? await PickSaveFileAsync($"{NewVaultName}.kdbx", "kdbx")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"{NewVaultName}.kdbx");

        if (string.IsNullOrWhiteSpace(savePath)) return;

        try
        {
            var db = KdbxDatabase.CreateDefault(NewVaultName);
            PopulateSampleEntries(db);

            var key = CompositeKey.FromPasswordAndKeyFile(UnlockMasterPassword, UnlockKeyFilePath, UnlockUseWindowsUserAccount && IsWindows);
            KdbxFormat.Save(db, savePath, key);
            ActivateDatabase(db, key);
            SettingsService.AddRecentVault(savePath, string.IsNullOrWhiteSpace(UnlockKeyFilePath) ? null : UnlockKeyFilePath, db.Meta.DatabaseName);
            RefreshRecentVaults();
            UnlockMasterPassword = string.Empty;
            ShowToast($"已创建并加密保存密码库：{Path.GetFileName(savePath)}");
        }
        catch (Exception ex)
        {
            UnlockError = $"创建密码库失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenDemoVault()
    {
        var db = KdbxDatabase.CreateDefault("个人密码库.kdbx");
        PopulateSampleEntries(db);
        ActivateDatabase(db, CompositeKey.FromPassword("demo"));
        ShowToast("已进入演示密码库（已载入全部 6 条示例密码）");
    }

    /// <summary>
    /// 填充设计稿（画板 02 / 12）中的完整子分组与 6 条示例条目，供新建密码库与演示模式共用。
    /// </summary>
    private static void PopulateSampleEntries(KdbxDatabase db)
    {
        var devGroup = db.RootGroup.Groups[2]; // 开发与云服务
        var sshSubGroup = new KdbxGroup { Name = "SSH 密钥与令牌", IconId = 3 };
        var prodDbSubGroup = new KdbxGroup { Name = "生产数据库", IconId = 3 };
        devGroup.AddGroup(sshSubGroup);
        devGroup.AddGroup(prodDbSubGroup);

        // 1. GitHub 企业版
        var e1 = new KdbxEntry
        {
            Title = "GitHub 企业版",
            UserName = "percy@company.com",
            Password = PasswordGenerator.GenerateFromCharset(new PasswordGeneratorOptions { Length = 22 }),
            Url = "https://github.com/login",
            Notes = "主组织管理员账号，已绑定硬件 YubiKey 5C 备用密钥。\nSSH 签名密钥按季度轮换（下次轮换时间：2027-01-15）。",
            IsFavorite = true
        };
        e1.Tags.AddRange(["工作", "代码"]);
        e1.SetString(KdbxConstants.FieldOtp, "otpauth://totp/GitHub:percy?secret=JBSWY3DPEHPK3PXP&issuer=GitHub", true);
        e1.SetString("API_Token", "ghp_live_demo_token_9821", true);
        e1.Binaries["github-恢复码.txt"] = new KeePassModern.Core.Crypto.ProtectedBinary(true, System.Text.Encoding.UTF8.GetBytes("REC-9941-8820-ABCD\nREC-4412-7765-EFGH"));
        sshSubGroup.AddEntry(e1);

        // 2. AWS 生产环境根账号
        var e2 = new KdbxEntry
        {
            Title = "AWS 生产环境根账号",
            UserName = "ops-admin@acme.io",
            Password = PasswordGenerator.GenerateFromCharset(new PasswordGeneratorOptions { Length = 26 }),
            Url = "https://console.aws.amazon.com",
            Notes = "生产环境高危账号，操作前请开启 CloudTrail 审计日志。",
            IsFavorite = true
        };
        e2.Tags.AddRange(["工作", "生产"]);
        e2.SetString(KdbxConstants.FieldOtp, "otpauth://totp/AWS:ops-admin?secret=MZXW6YTBOI======&issuer=AWS", true);
        devGroup.AddEntry(e2);

        // 3. Cloudflare 域名解析
        var e3 = new KdbxEntry
        {
            Title = "Cloudflare 域名解析",
            UserName = "percy@dev.io",
            Password = PasswordGenerator.GenerateFromCharset(new PasswordGeneratorOptions { Length = 20 }),
            Url = "https://dash.cloudflare.com",
            Notes = "主域名 DNS 与 Zero Trust 隧道控制台。"
        };
        e3.Tags.AddRange(["工作", "生产"]);
        e3.SetString(KdbxConstants.FieldOtp, "otpauth://totp/Cloudflare:percy?secret=NB2W45DFOIZA====&issuer=Cloudflare", true);
        devGroup.AddEntry(e3);

        // 4. PostgreSQL 生产数据库（即将过期 + 弱密码示例，便于体验安全审计）
        var e4 = new KdbxEntry
        {
            Title = "PostgreSQL 生产数据库",
            UserName = "db_readonly",
            Password = "WeakPassword123",
            Url = "postgres://db.internal:5432/prod",
            Expires = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(5),
            Notes = "季度轮换只读凭证，将在 5 天后到期。"
        };
        e4.Tags.Add("生产");
        prodDbSubGroup.AddEntry(e4);

        // 5. Stripe 支付控制台
        var e5 = new KdbxEntry
        {
            Title = "Stripe 支付控制台",
            UserName = "billing@company.com",
            Password = PasswordGenerator.GenerateFromCharset(new PasswordGeneratorOptions { Length = 24 }),
            Url = "https://dashboard.stripe.com",
            Notes = "公司收款与账单管理控制台。",
            IsFavorite = true
        };
        e5.Tags.AddRange(["工作", "个人"]);
        e5.SetString(KdbxConstants.FieldOtp, "otpauth://totp/Stripe:billing?secret=KRUGS4ZANFZSAYJA&issuer=Stripe", true);
        db.RootGroup.Groups[3].AddEntry(e5); // 金融与银行

        // 6. Linear 项目协作
        var e6 = new KdbxEntry
        {
            Title = "Linear 项目协作",
            UserName = "percy@company.com",
            Password = PasswordGenerator.GenerateFromCharset(new PasswordGeneratorOptions { Length = 20 }),
            Url = "https://linear.app",
            Notes = "研发迭代与需求追踪平台。"
        };
        e6.Tags.Add("工作");
        db.RootGroup.Groups[0].AddEntry(e6); // 常用账号
    }

    private void ActivateDatabase(KdbxDatabase db, CompositeKey key)
    {
        // 如果已打开相同路径的密码库，直接切换过去，避免重复打开
        if (!string.IsNullOrWhiteSpace(db.FilePath))
        {
            var existing = OpenVaults.FirstOrDefault(s =>
                string.Equals(s.Database.FilePath, db.FilePath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                db.Dispose();
                key.Dispose();
                SwitchVaultTab(existing);
                return;
            }
        }

        var display = string.IsNullOrWhiteSpace(db.FilePath) ? db.Meta.DatabaseName : Path.GetFileName(db.FilePath);
        var session = new VaultTabSession(db, key, display);
        OpenVaults.Add(session);
        OnPropertyChanged(nameof(HasMultipleVaults));
        SwitchVaultTab(session);
    }

    [RelayCommand]
    private void SwitchVaultTab(VaultTabSession? session)
    {
        if (session is null || session == _activeSession) return;

        // 保存当前标签页的视图状态
        if (_activeSession is not null)
        {
            _activeSession.SavedSection = ActiveSection;
            _activeSession.SavedGroupUuid = SelectedGroup?.Model.Uuid;
            _activeSession.SavedEntryUuid = SelectedEntry?.Model.Uuid;
            _activeSession.IsActive = false;
        }

        _activeSession = session;
        session.IsActive = true;
        _database = session.Database;
        _masterKey = session.MasterKey;
        VaultDisplayName = session.DisplayName;
        IsUnlocked = true;
        RecordUserActivity();

        ActiveSection = session.SavedSection;
        IsAuditViewActive = ActiveSection == ActiveSection.SecurityAudit;
        RebuildWorkspace();

        if (session.SavedGroupUuid.HasValue)
            SelectedGroup = FindGroupVm(RootGroups, session.SavedGroupUuid.Value);
        if (session.SavedEntryUuid.HasValue)
            SelectedEntry = FilteredEntries.FirstOrDefault(e => e.Model.Uuid == session.SavedEntryUuid.Value) ?? SelectedEntry;
    }

    private static GroupItemViewModel? FindGroupVm(IEnumerable<GroupItemViewModel> list, Guid uuid)
    {
        foreach (var g in list)
        {
            if (g.Model.Uuid == uuid) return g;
            var child = FindGroupVm(g.Children, uuid);
            if (child is not null) return child;
        }
        return null;
    }

    [RelayCommand]
    private void CloseVaultTab(VaultTabSession? session)
    {
        var target = session ?? _activeSession;
        if (target is null) return;

        if (target.Database.IsModified && !string.IsNullOrEmpty(target.Database.FilePath))
        {
            try { KdbxFormat.Save(target.Database, target.Database.FilePath, target.MasterKey); } catch { }
        }

        var closedName = target.DisplayName;
        target.Database.Dispose();
        target.MasterKey.Dispose();
        OpenVaults.Remove(target);
        OnPropertyChanged(nameof(HasMultipleVaults));

        if (OpenVaults.Count > 0)
        {
            if (target == _activeSession)
            {
                _activeSession = null;
                SwitchVaultTab(OpenVaults[^1]);
            }
            ShowToast($"已关闭并锁定密码库：{closedName}");
        }
        else
        {
            _activeSession = null;
            _database = null;
            _masterKey = null;
            FilteredEntries.Clear();
            RootGroups.Clear();
            AllTags.Clear();
            SelectedEntry = null;
            IsUnlocked = false;
            IsModified = false;
            ShowToast($"已关闭并锁定密码库：{closedName}");
        }
    }

    [RelayCommand]
    private async Task OpenAnotherVaultAsync()
    {
        if (PickOpenFileAsync is null || OpenVaultUnlockPromptAsync is null) return;
        var path = await PickOpenFileAsync("kdbx");
        if (string.IsNullOrWhiteSpace(path)) return;

        var existing = OpenVaults.FirstOrDefault(s =>
            string.Equals(s.Database.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SwitchVaultTab(existing);
            ShowToast($"已切换到已打开的密码库：{existing.DisplayName}");
            return;
        }

        var res = await OpenVaultUnlockPromptAsync(path, "打开其他密码库", "解锁并打开");
        if (res is null) return;

        ActivateDatabase(res.Value.Database, res.Value.MasterKey);
        SettingsService.AddRecentVault(path, string.IsNullOrWhiteSpace(res.Value.KeyFilePath) ? null : res.Value.KeyFilePath, res.Value.Database.Meta.DatabaseName);
        RefreshRecentVaults();
        ShowToast($"已在新标签页打开：{Path.GetFileName(path)}");
    }

    [RelayCommand]
    private async Task SaveVaultAsAsync()
    {
        if (_database is null || _masterKey is null || PickSaveFileAsync is null) return;
        var suggested = string.IsNullOrWhiteSpace(_database.FilePath)
            ? $"{_database.Meta.DatabaseName}.kdbx"
            : Path.GetFileName(_database.FilePath);
        var path = await PickSaveFileAsync(suggested, "kdbx");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            KdbxFormat.Save(_database, path, _masterKey);
            IsModified = false;
            VaultDisplayName = Path.GetFileName(path);
            if (_activeSession is not null) _activeSession.DisplayName = VaultDisplayName;
            SettingsService.AddRecentVault(path, null, _database.Meta.DatabaseName);
            RefreshRecentVaults();
            ShowToast($"已另存为：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            ShowToast($"另存为失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void LockVault()
    {
        foreach (var session in OpenVaults.ToList())
        {
            if (session.Database.IsModified && !string.IsNullOrEmpty(session.Database.FilePath))
            {
                try { KdbxFormat.Save(session.Database, session.Database.FilePath, session.MasterKey); } catch { }
            }
            session.Database.Dispose();
            session.MasterKey.Dispose();
        }
        OpenVaults.Clear();
        OnPropertyChanged(nameof(HasMultipleVaults));
        _activeSession = null;
        _database = null;
        _masterKey = null;
        FilteredEntries.Clear();
        RootGroups.Clear();
        AllTags.Clear();
        SelectedEntry = null;
        IsUnlocked = false;
        IsModified = false;
        ShowToast("全部密码库已锁定，内存敏感数据已清零");
    }

    [RelayCommand]
    private void SaveVault()
    {
        if (_database is null || _masterKey is null) return;
        if (string.IsNullOrWhiteSpace(_database.FilePath))
        {
            ShowToast("当前为演示库，如需持久化请先新建正式 .kdbx 密码库");
            return;
        }

        try
        {
            KdbxFormat.Save(_database, _database.FilePath, _masterKey);
            IsModified = false;
            ShowToast("密码库已安全保存（KDBX 4）");
        }
        catch (Exception ex)
        {
            ShowToast($"保存失败：{ex.Message}");
        }
    }

    public void RebuildWorkspace()
    {
        if (_database is null) return;

        IsModified = _database.IsModified;
        var all = _database.EnumerateAllEntries(includeRecycleBin: false).ToList();
        AllCount = all.Count;
        FavoritesCount = all.Count(e => e.IsFavorite);
        TotpCount = all.Count(e => e.HasTotp());

        var bin = _database.Meta.RecycleBinUuid != Guid.Empty ? _database.FindGroupByUuid(_database.Meta.RecycleBinUuid) : null;
        RecycleBinCount = bin?.Entries.Count ?? 0;

        // Audit report
        var audit = SecurityAuditService.Analyze(_database);
        AuditScore = audit.SecurityScore;
        AuditIssueCount = audit.TotalIssues;
        UpdateObservable(AuditWeakEntries, audit.WeakPasswords);
        UpdateObservable(AuditReusedEntries, audit.ReusedPasswords);
        UpdateObservable(AuditExpiringEntries, audit.ExpiredOrExpiringSoon);
        UpdateObservable(AuditNo2FaEntries, audit.MissingTwoFactor);

        // Groups
        RootGroups.Clear();
        foreach (var g in _database.RootGroup.Groups)
        {
            if (_database.Meta.RecycleBinEnabled && g.Uuid == _database.Meta.RecycleBinUuid) continue;
            RootGroups.Add(new GroupItemViewModel(g, _database.Meta.RecycleBinUuid));
        }

        // Tags
        AllTags.Clear();
        foreach (var tag in all.SelectMany(e => e.Tags)
                     .Where(t => !string.Equals(t, "Favorite", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(t => t))
        {
            AllTags.Add(tag);
        }

        RefreshFilteredEntries();
    }

    partial void OnSearchQueryChanged(string value) => RefreshFilteredEntries();

    partial void OnSelectedGroupChanged(GroupItemViewModel? value)
    {
        if (value is not null)
        {
            ActiveSection = ActiveSection.Group;
            IsAuditViewActive = false;
            RefreshFilteredEntries();
        }
    }

    partial void OnActiveSectionChanged(ActiveSection value)
    {
        OnPropertyChanged(nameof(IsAllEntriesSelected));
        OnPropertyChanged(nameof(IsFavoritesSelected));
        OnPropertyChanged(nameof(IsTotpSelected));
        OnPropertyChanged(nameof(IsAuditSelected));
        OnPropertyChanged(nameof(IsRecycleBinSelected));
    }

    partial void OnSortByModifiedDescChanged(bool value)
    {
        OnPropertyChanged(nameof(SortButtonLabel));
        RefreshFilteredEntries();
    }

    [RelayCommand]
    private void ToggleSortOrder() => SortByModifiedDesc = !SortByModifiedDesc;

    [RelayCommand]
    private void CycleDensity()
    {
        var next = DensityService.Current switch
        {
            UiDensity.Compact => UiDensity.Normal,
            UiDensity.Normal => UiDensity.Comfortable,
            _ => UiDensity.Compact
        };
        SetDensity(next.ToString());
    }

    [RelayCommand]
    private void SetDensity(string? densityName)
    {
        if (!Enum.TryParse<UiDensity>(densityName, ignoreCase: true, out var target))
            target = UiDensity.Normal;
        DensityService.Apply(target);
        SettingsService.Settings.UiDensity = target.ToString();
        SettingsService.Save();
        CurrentDensityLabel = target switch
        {
            UiDensity.Compact => "紧凑",
            UiDensity.Comfortable => "宽松",
            _ => "标准"
        };
        ShowToast($"已切换界面密度：{CurrentDensityLabel}");
    }

    [RelayCommand]
    private void SelectSection(string sectionName)
    {
        if (!Enum.TryParse<ActiveSection>(sectionName, out var sec)) return;
        ActiveSection = sec;
        SelectedGroup = null;
        _selectedTag = null;
        IsAuditViewActive = sec == ActiveSection.SecurityAudit;
        RefreshFilteredEntries();
    }

    [RelayCommand]
    private void SelectTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        _selectedTag = tag;
        ActiveSection = ActiveSection.Tag;
        IsAuditViewActive = false;
        RefreshFilteredEntries();
    }

    [RelayCommand]
    private void JumpToAuditEntry(EntryItemViewModel? item)
    {
        if (item is null) return;
        ActiveSection = ActiveSection.AllEntries;
        IsAuditViewActive = false;
        RefreshFilteredEntries();
        SelectedEntry = FilteredEntries.FirstOrDefault(e => e.Model.Uuid == item.Model.Uuid);
    }

    private void RefreshFilteredEntries()
    {
        if (_database is null) return;
        var prevSelectedUuid = SelectedEntry?.Model.Uuid;

        IEnumerable<KdbxEntry> source = ActiveSection switch
        {
            ActiveSection.Favorites => _database.EnumerateAllEntries(false).Where(e => e.IsFavorite),
            ActiveSection.TotpOnly => _database.EnumerateAllEntries(false).Where(e => e.HasTotp()),
            ActiveSection.RecycleBin => (_database.FindGroupByUuid(_database.Meta.RecycleBinUuid)?.Entries ?? Enumerable.Empty<KdbxEntry>()),
            ActiveSection.Group when SelectedGroup is not null => EnumerateGroupEntries(SelectedGroup.Model),
            ActiveSection.Tag when !string.IsNullOrEmpty(_selectedTag) => _database.EnumerateAllEntries(false)
                .Where(e => e.Tags.Any(t => string.Equals(t, _selectedTag, StringComparison.OrdinalIgnoreCase))),
            ActiveSection.CustomFilter when _customFilterEntries is not null => _customFilterEntries,
            _ => _database.EnumerateAllEntries(false)
        };

        CurrentFilterTitle = ActiveSection switch
        {
            ActiveSection.Favorites => "收藏夹",
            ActiveSection.TotpOnly => "动态验证码 (2FA)",
            ActiveSection.RecycleBin => "回收站",
            ActiveSection.Group when SelectedGroup is not null => SelectedGroup.Name,
            ActiveSection.Tag => $"#{_selectedTag}",
            ActiveSection.CustomFilter => _customFilterTitle,
            _ => "全部条目"
        };

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            source = source.Where(e =>
                e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.UserName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Url.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Notes.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        var list = (SortByModifiedDesc
            ? source.OrderByDescending(e => e.LastModificationTime)
            : source.OrderByDescending(e => e.IsFavorite).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)).ToList();
        FilteredEntries.Clear();
        foreach (var e in list)
            FilteredEntries.Add(new EntryItemViewModel(e));

        SelectedEntry = FilteredEntries.FirstOrDefault(x => x.Model.Uuid == prevSelectedUuid) ?? FilteredEntries.FirstOrDefault();
    }

    private static IEnumerable<KdbxEntry> EnumerateGroupEntries(KdbxGroup g)
    {
        foreach (var e in g.Entries) yield return e;
        foreach (var sub in g.Groups)
            foreach (var e in EnumerateGroupEntries(sub))
                yield return e;
    }

    [RelayCommand]
    private async Task AddEntryAsync()
    {
        if (_database is null || OpenEntryEditorAsync is null) return;
        var targetGroup = SelectedGroup?.Model ?? _database.RootGroup.Groups.FirstOrDefault() ?? _database.RootGroup;
        if (await OpenEntryEditorAsync(null, targetGroup))
        {
            _database.IsModified = true;
            SaveVault();
            RebuildWorkspace();
        }
    }

    [RelayCommand]
    private async Task EditSelectedEntryAsync() => await EditEntryAsync(SelectedEntry);

    [RelayCommand]
    private async Task EditEntryAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null || OpenEntryEditorAsync is null) return;
        var targetGroup = target.Model.Parent ?? _database.RootGroup;
        if (await OpenEntryEditorAsync(target.Model, targetGroup))
        {
            _database.IsModified = true;
            SaveVault();
            RebuildWorkspace();
        }
    }

    [RelayCommand]
    private void DuplicateEntry(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null) return;
        var clone = _database.DuplicateEntry(target.Model);
        SaveVault();
        RebuildWorkspace();
        SelectedEntry = FilteredEntries.FirstOrDefault(e => e.Model.Uuid == clone.Uuid) ?? SelectedEntry;
        ShowToast($"已克隆条目：{clone.Title}");
    }

    [RelayCommand]
    private void ToggleFavoriteSelected() => ToggleFavoriteEntry(SelectedEntry);

    [RelayCommand]
    private void ToggleFavoriteEntry(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null) return;
        target.Model.IsFavorite = !target.Model.IsFavorite;
        target.Model.LastModificationTime = DateTimeOffset.UtcNow;
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
    }

    [RelayCommand]
    private void DeleteSelectedEntry() => DeleteEntry(SelectedEntry);

    [RelayCommand]
    private void DeleteEntry(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null) return;
        bool wasInBin = _database.IsInRecycleBin(target.Model);
        _database.MoveEntryToRecycleBin(target.Model);
        SaveVault();
        RebuildWorkspace();
        ShowToast(wasInBin ? "条目已永久删除" : "条目已移入回收站");
    }

    [RelayCommand]
    private void PermanentlyDeleteEntry(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null) return;
        _database.DeleteEntryPermanently(target.Model);
        SaveVault();
        RebuildWorkspace();
        ShowToast("条目已永久删除");
    }

    [RelayCommand]
    private void RestoreSelectedEntry() => RestoreEntry(SelectedEntry);

    [RelayCommand]
    private void RestoreEntry(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null) return;
        if (_database.RestoreEntryFromRecycleBin(target.Model))
        {
            SaveVault();
            RebuildWorkspace();
            ShowToast("条目已恢复到原分组");
        }
    }

    [RelayCommand]
    private void EmptyRecycleBin()
    {
        if (_database is null || RecycleBinCount == 0) return;
        _database.EmptyRecycleBin();
        SaveVault();
        RebuildWorkspace();
        ShowToast("回收站已清空");
    }

    // ==================== 分组管理（新建 / 新建子分组 / 重命名 / 删除） ====================
    [RelayCommand]
    private async Task AddRootGroupAsync()
    {
        if (_database is null || OpenGroupDialogAsync is null) return;
        var res = await OpenGroupDialogAsync("新建根分组", string.Empty, string.Empty);
        if (res is null) return;

        var g = new KdbxGroup { Name = res.Value.Name, Notes = res.Value.Notes, IconId = 48 };
        _database.RootGroup.AddGroup(g);
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast($"已新建分组：{g.Name}");
    }

    [RelayCommand]
    private async Task AddSubGroupAsync(GroupItemViewModel? parent)
    {
        if (_database is null || parent is null || OpenGroupDialogAsync is null) return;
        var res = await OpenGroupDialogAsync($"在「{parent.Name}」下新建子分组", string.Empty, string.Empty);
        if (res is null) return;

        var g = new KdbxGroup { Name = res.Value.Name, Notes = res.Value.Notes, IconId = 48 };
        parent.Model.AddGroup(g);
        parent.Model.IsExpanded = true;
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast($"已新建子分组：{g.Name}");
    }

    [RelayCommand]
    private async Task RenameGroupAsync(GroupItemViewModel? group)
    {
        if (_database is null || group is null || OpenGroupDialogAsync is null) return;
        var res = await OpenGroupDialogAsync("重命名分组", group.Model.Name, group.Model.Notes);
        if (res is null) return;

        group.Model.Name = res.Value.Name;
        group.Model.Notes = res.Value.Notes;
        group.Model.LastModificationTime = DateTimeOffset.UtcNow;
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast($"已更新分组：{group.Model.Name}");
    }

    [RelayCommand]
    private void DeleteGroup(GroupItemViewModel? group)
    {
        if (_database is null || group is null) return;
        var name = group.Model.Name;
        if (SelectedGroup?.Model.Uuid == group.Model.Uuid)
        {
            SelectedGroup = null;
            ActiveSection = ActiveSection.AllEntries;
        }
        _database.MoveGroupToRecycleBin(group.Model);
        SaveVault();
        RebuildWorkspace();
        ShowToast(_database.Meta.RecycleBinEnabled ? $"分组「{name}」已移入回收站" : $"分组「{name}」已删除");
    }

    [RelayCommand]
    private void MoveGroupUp(GroupItemViewModel? group) => ReorderGroup(group, -1);

    [RelayCommand]
    private void MoveGroupDown(GroupItemViewModel? group) => ReorderGroup(group, 1);

    private void ReorderGroup(GroupItemViewModel? group, int delta)
    {
        if (_database is null || group?.Model.Parent is null) return;
        var siblings = group.Model.Parent.Groups;
        int idx = siblings.IndexOf(group.Model);
        int targetIdx = idx + delta;
        if (idx < 0 || targetIdx < 0 || targetIdx >= siblings.Count) return;

        siblings.RemoveAt(idx);
        siblings.Insert(targetIdx, group.Model);
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
    }

    [RelayCommand]
    private void SortSubGroups(GroupItemViewModel? group)
    {
        if (_database is null || group is null || group.Model.Groups.Count < 2) return;
        var sorted = group.Model.Groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        group.Model.Groups.Clear();
        group.Model.Groups.AddRange(sorted);
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast($"已按名称排序「{group.Name}」的子分组");
    }

    // ==================== 条目多选批量操作 ====================
    [RelayCommand]
    private async Task BatchMoveEntriesAsync()
    {
        if (_database is null || _selectedEntries.Count == 0 || OpenMoveToGroupAsync is null) return;
        var dest = await OpenMoveToGroupAsync(_database, _selectedEntries[0].Model);
        if (dest is null) return;

        int moved = 0;
        foreach (var item in _selectedEntries.ToList())
        {
            if (DatabaseMaintenanceService.MoveEntryToGroup(_database, item.Model, dest))
                moved++;
        }
        if (moved > 0)
        {
            SaveVault();
            RebuildWorkspace();
            ShowToast($"已批量将 {moved} 条记录移动到分组「{dest.Name}」");
        }
    }

    [RelayCommand]
    private void BatchFavoriteEntries()
    {
        if (_database is null || _selectedEntries.Count == 0) return;
        bool targetFav = _selectedEntries.Any(e => !e.Model.IsFavorite);
        foreach (var item in _selectedEntries)
        {
            item.Model.IsFavorite = targetFav;
            item.Model.LastModificationTime = DateTimeOffset.UtcNow;
        }
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast(targetFav ? $"已批量收藏 {_selectedEntries.Count} 条记录" : $"已批量取消收藏 {_selectedEntries.Count} 条记录");
    }

    [RelayCommand]
    private void BatchDeleteEntries()
    {
        if (_database is null || _selectedEntries.Count == 0) return;
        int count = _selectedEntries.Count;
        foreach (var item in _selectedEntries.ToList())
            _database.MoveEntryToRecycleBin(item.Model);
        SaveVault();
        RebuildWorkspace();
        ShowToast(ActiveSection == ActiveSection.RecycleBin ? $"已永久删除 {count} 条记录" : $"已将 {count} 条记录移入回收站");
    }

    [RelayCommand]
    private void ToggleRevealPassword()
    {
        if (SelectedEntry is not null)
            SelectedEntry.IsPasswordRevealed = !SelectedEntry.IsPasswordRevealed;
    }

    [RelayCommand]
    private async Task CopyUserNameAsync() => await CopyEntryUserNameAsync(SelectedEntry);

    [RelayCommand]
    private async Task CopyEntryUserNameAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null) return;
        var resolved = FieldReferenceResolver.Resolve(target.UserName, target.Model, _database);
        await CopySecretAsync(resolved, "用户名");
        if (target == SelectedEntry) FlashCopiedFlag(v => IsUserNameCopied = v);
    }

    [RelayCommand]
    private async Task CopyPasswordAsync() => await CopyEntryPasswordAsync(SelectedEntry);

    [RelayCommand]
    private async Task CopyEntryPasswordAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null) return;
        var resolved = FieldReferenceResolver.Resolve(target.Password, target.Model, _database);
        await CopySecretAsync(resolved, "密码");
        if (target == SelectedEntry) FlashCopiedFlag(v => IsPasswordCopied = v);
    }

    [RelayCommand]
    private async Task CopyTotpAsync() => await CopyEntryTotpAsync(SelectedEntry);

    [RelayCommand]
    private async Task CopyEntryTotpAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null || !target.HasTotp) return;
        await CopySecretAsync(target.TotpRaw, "动态验证码");
        if (target == SelectedEntry) FlashCopiedFlag(v => IsTotpCopied = v);
    }

    [RelayCommand]
    private async Task CopyEntryUrlAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null || string.IsNullOrWhiteSpace(target.Url)) return;
        var resolved = FieldReferenceResolver.Resolve(target.Url, target.Model, _database);
        await CopySecretAsync(resolved, "网址");
    }

    [RelayCommand]
    private void OpenEntryUrl(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null || string.IsNullOrWhiteSpace(target.Url)) return;
        var resolved = FieldReferenceResolver.Resolve(target.Url, target.Model, _database);
        OpenUrlExternal?.Invoke(resolved);
    }

    [RelayCommand]
    private async Task PerformAutoTypeAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null) return;

        var res = await AutoTypeService.ExecuteAsync(target.Model, _database, MinimizeWindowAction);
        ShowToast(res.Message);
    }

    /// <summary>
    /// 全局 Auto-Type 热键触发：探测当前前台窗口标题，自动匹配条目（单条直接输入，多条弹窗选择）。
    /// </summary>
    [RelayCommand]
    private async Task TriggerGlobalAutoTypeAsync()
    {
        if (!IsUnlocked || _database is null) return;
        var windowTitle = await Task.Run(GlobalHotKeyService.GetForegroundWindowTitle);
        var matches = AutoTypeMatcher.FindMatches(_database, windowTitle);

        if (matches.Count == 1)
        {
            var res = await AutoTypeService.ExecuteAsync(matches[0].Entry, _database);
            ShowToast(res.Message);
            return;
        }

        if (matches.Count == 0)
        {
            // 若无精确标题匹配，列出全部启用了 Auto-Type 的条目供快速选择
            matches = _database.EnumerateAllEntries(false)
                .Where(e => !e.IsExpired && e.AutoType.Enabled)
                .Select(e => new AutoTypeMatchResult(e, AutoTypeSequenceParser.GetEffectiveSequence(e), "手动选择"))
                .ToList();
        }

        if (matches.Count == 0 || OpenAutoTypePickerAsync is null) return;
        var picked = await OpenAutoTypePickerAsync(windowTitle, matches);
        if (picked is null) return;

        var execRes = await AutoTypeService.ExecuteAsync(picked.Entry, _database);
        ShowToast(execRes.Message);
    }

    [RelayCommand]
    private async Task CopyCustomFieldAsync(CustomFieldItem? field)
    {
        if (field is null || SelectedEntry is null) return;
        var resolved = FieldReferenceResolver.Resolve(field.RawValue, SelectedEntry.Model, _database);
        await CopySecretAsync(resolved, $"字段「{field.Key}」");
    }

    [RelayCommand]
    private async Task ExportAttachmentAsync(AttachmentItem? attachment)
    {
        if (attachment is null || SelectedEntry is null || PickSaveFileAsync is null) return;
        if (!SelectedEntry.Model.Binaries.TryGetValue(attachment.FileName, out var binary)) return;

        var ext = Path.GetExtension(attachment.FileName).TrimStart('.');
        var path = await PickSaveFileAsync(attachment.FileName, string.IsNullOrEmpty(ext) ? "*" : ext);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            await File.WriteAllBytesAsync(path, binary.ReadData());
            ShowToast($"已导出附件：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            ShowToast($"导出附件失败：{ex.Message}");
        }
    }

    // ==================== 密码库设置 / 修改主密钥 / 合并同步 ====================
    [RelayCommand]
    private async Task OpenDatabaseSettingsAsyncCommand()
    {
        if (_database is null || OpenDatabaseSettingsAsync is null) return;
        var (saved, newKey, newKeyFile) = await OpenDatabaseSettingsAsync(_database);
        if (!saved) return;

        if (newKey is not null)
        {
            _masterKey?.Dispose();
            _masterKey = newKey;
            if (_activeSession is not null) _activeSession.MasterKey = newKey;
            if (!string.IsNullOrWhiteSpace(_database.FilePath))
                SettingsService.AddRecentVault(_database.FilePath, newKeyFile, _database.Meta.DatabaseName);
        }

        VaultDisplayName = string.IsNullOrWhiteSpace(_database.FilePath)
            ? _database.Meta.DatabaseName
            : Path.GetFileName(_database.FilePath);
        if (_activeSession is not null) _activeSession.DisplayName = VaultDisplayName;
        SaveVault();
        RebuildWorkspace();
        ShowToast(newKey is not null ? "密码库设置与主密钥已更新并重新加密保存" : "密码库设置已更新并保存");
    }

    [RelayCommand]
    private async Task MergeDatabaseAsync()
    {
        if (_database is null || PickOpenFileAsync is null || OpenVaultUnlockPromptAsync is null) return;
        var path = await PickOpenFileAsync("kdbx");
        if (string.IsNullOrWhiteSpace(path)) return;

        var res = await OpenVaultUnlockPromptAsync(path, "合并 / 同步其他密码库", "解密并合并");
        if (res is null) return;

        using var sourceDb = res.Value.Database;
        using var sourceKey = res.Value.MasterKey;
        var summary = DatabaseMergeService.Merge(_database, sourceDb);
        SaveVault();
        RebuildWorkspace();
        ShowToast($"合并完成：新增 {summary.EntriesAdded} 条目、更新 {summary.EntriesUpdated} 条目、新增 {summary.GroupsAdded} 分组");
    }

    [RelayCommand]
    private async Task MoveEntryToGroupAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null || OpenMoveToGroupAsync is null) return;
        var dest = await OpenMoveToGroupAsync(_database, target.Model);
        if (dest is null) return;

        if (DatabaseMaintenanceService.MoveEntryToGroup(_database, target.Model, dest))
        {
            SaveVault();
            RebuildWorkspace();
            ShowToast($"已将「{target.Title}」移动到分组「{dest.Name}」");
        }
    }

    /// <summary>
    /// 供拖拽条目到左侧分组树时直接调用。
    /// </summary>
    public void MoveEntryByUuidToGroup(Guid entryUuid, GroupItemViewModel targetGroup)
    {
        if (_database is null) return;
        var entry = _database.FindEntryByUuid(entryUuid);
        if (entry is null) return;
        if (DatabaseMaintenanceService.MoveEntryToGroup(_database, entry, targetGroup.Model))
        {
            SaveVault();
            RebuildWorkspace();
            ShowToast($"已将「{entry.Title}」移动到分组「{targetGroup.Name}」");
        }
    }

    [RelayCommand]
    private async Task OpenDatabaseMaintenanceAsyncCommand()
    {
        if (_database is null || OpenDatabaseMaintenanceAsync is null) return;
        if (await OpenDatabaseMaintenanceAsync(_database))
        {
            SaveVault();
            RebuildWorkspace();
            ShowToast("密码库维护完成并已保存");
        }
    }

    // ==================== 高级查找 / 预置快捷筛选 / 快捷过期编辑 / KeePass XML ====================
    [RelayCommand]
    private async Task OpenAdvancedSearchAsyncCommand()
    {
        if (_database is null || OpenAdvancedSearchAsync is null) return;
        var param = await OpenAdvancedSearchAsync();
        if (param is null) return;

        _customFilterEntries = EntrySearchService.Search(_database, param);
        _customFilterTitle = string.IsNullOrWhiteSpace(param.Query)
            ? "高级查找结果"
            : $"查找「{param.Query}」";
        ActiveSection = ActiveSection.CustomFilter;
        SelectedGroup = null;
        IsAuditViewActive = false;
        RefreshFilteredEntries();
        ShowToast($"高级查找完成，共匹配 {_customFilterEntries.Count} 条记录");
    }

    [RelayCommand]
    private void ShowQuickFilter(string? preset)
    {
        if (_database is null || string.IsNullOrWhiteSpace(preset)) return;

        (_customFilterTitle, _customFilterEntries) = preset switch
        {
            "Expired" => ("已过期条目", EntrySearchService.FindExpired(_database)),
            "Expiring7" => ("7 天内即将过期", EntrySearchService.FindExpiringWithinDays(_database, 7)),
            "Expiring30" => ("30 天内即将过期", EntrySearchService.FindExpiringWithinDays(_database, 30)),
            "LastModified" => ("最近修改的条目", EntrySearchService.FindLastModified(_database, 30)),
            "Large" => ("大体积 / 含附件条目", EntrySearchService.FindLargeEntries(_database, 30)),
            _ => ("全部条目", _database.EnumerateAllEntries(false).ToList())
        };

        ActiveSection = ActiveSection.CustomFilter;
        SelectedGroup = null;
        IsAuditViewActive = false;
        RefreshFilteredEntries();
    }

    [RelayCommand]
    private void QuickExpireNow(EntryItemViewModel? item) => ApplyQuickExpiry(item, true, DateTimeOffset.UtcNow.AddMinutes(-1), "已设为立即过期");

    [RelayCommand]
    private void QuickExpireNever(EntryItemViewModel? item) => ApplyQuickExpiry(item, false, DateTimeOffset.UtcNow.AddYears(1), "已设为永不过期");

    [RelayCommand]
    private void QuickExpireIn30Days(EntryItemViewModel? item) => ApplyQuickExpiry(item, true, DateTimeOffset.UtcNow.AddDays(30), "已设为 30 天后过期");

    [RelayCommand]
    private void QuickExpireIn90Days(EntryItemViewModel? item) => ApplyQuickExpiry(item, true, DateTimeOffset.UtcNow.AddDays(90), "已设为 90 天后过期");

    private void ApplyQuickExpiry(EntryItemViewModel? item, bool expires, DateTimeOffset expiryTime, string toast)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null) return;
        target.Model.Expires = expires;
        target.Model.ExpiryTime = expiryTime;
        target.Model.LastModificationTime = DateTimeOffset.UtcNow;
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast($"「{target.Title}」{toast}");
    }

    [RelayCommand]
    private async Task ExportKeePassXmlAsync()
    {
        if (_database is null || PickSaveFileAsync is null) return;
        var path = await PickSaveFileAsync($"{_database.Meta.DatabaseName}.xml", "xml");
        if (string.IsNullOrWhiteSpace(path)) return;
        await File.WriteAllTextAsync(path, ImportExportService.ExportToKeePassXml(_database));
        ShowToast($"已导出标准 KeePass 2.x XML：{Path.GetFileName(path)}");
    }

    [RelayCommand]
    private async Task ImportKeePassXmlAsync()
    {
        if (_database is null || PickOpenFileAsync is null) return;
        var path = await PickOpenFileAsync("xml");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var xml = await File.ReadAllTextAsync(path);
            var summary = ImportExportService.ImportFromKeePassXml(_database, xml);
            SaveVault();
            RebuildWorkspace();
            ShowToast($"已导入 KeePass XML：新增 {summary.EntriesAdded} 条目、更新 {summary.EntriesUpdated} 条目");
        }
        catch (Exception ex)
        {
            ShowToast($"导入 XML 失败：{ex.Message}");
        }
    }

    private static void FlashCopiedFlag(Action<bool> setter)
    {
        setter(true);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() => setter(false));
        });
    }

    [RelayCommand]
    private async Task OpenPasswordGeneratorCommandAsync()
    {
        if (OpenPasswordGeneratorAsync is null) return;
        var generated = await OpenPasswordGeneratorAsync();
        if (!string.IsNullOrEmpty(generated))
            await CopySecretAsync(generated, "生成的新密码");
    }

    [RelayCommand]
    private async Task OpenSettingsCommandAsync()
    {
        if (OpenSettingsAsync is not null)
            await OpenSettingsAsync();
    }

    [RelayCommand]
    private async Task OpenAboutAsyncCommand()
    {
        if (OpenAboutAsync is not null)
            await OpenAboutAsync();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        await CheckForUpdatesCoreAsync(silent: false);
    }

    public async Task CheckForUpdatesCoreAsync(bool silent)
    {
        try
        {
            var current = AppVersionProvider.GetCurrentVersion();
            var updateService = new UpdateService();
            var release = await updateService.CheckForUpdatesAsync(current);
            if (release is not null && ShowUpdateWindowAsync is not null)
            {
                await ShowUpdateWindowAsync(release);
            }
            else if (!silent)
            {
                ShowToast($"当前已是最新版本（v{current}）");
            }
        }
        catch (Exception ex)
        {
            if (!silent)
                ShowToast($"检查更新失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke();

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (_database is null || PickSaveFileAsync is null) return;
        var path = await PickSaveFileAsync($"{_database.Meta.DatabaseName}.csv", "csv");
        if (string.IsNullOrWhiteSpace(path)) return;
        await File.WriteAllTextAsync(path, ImportExportService.ExportToCsv(_database));
        ShowToast($"已导出 CSV：{Path.GetFileName(path)}");
    }

    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        if (_database is null || PickOpenFileAsync is null) return;
        var path = await PickOpenFileAsync("csv");
        if (string.IsNullOrWhiteSpace(path)) return;
        var text = await File.ReadAllTextAsync(path);
        int count = ImportExportService.ImportFromCsv(_database, text);
        RebuildWorkspace();
        ShowToast($"已从 CSV 成功导入 {count} 条记录");
    }

    [RelayCommand]
    private async Task ExportEmergencySheetAsync()
    {
        if (_database is null || PickSaveFileAsync is null) return;
        var path = await PickSaveFileAsync($"{_database.Meta.DatabaseName}-应急表.html", "html");
        if (string.IsNullOrWhiteSpace(path)) return;
        await File.WriteAllTextAsync(path, ImportExportService.ExportEmergencySheetHtml(_database));
        ShowToast($"已生成应急表：{Path.GetFileName(path)}");
    }

    private async Task CopySecretAsync(string value, string label)
    {
        if (CopyToClipboardAsync is null || string.IsNullOrEmpty(value)) return;
        await CopyToClipboardAsync(value);
        _lastCopiedSecret = value;

        int seconds = Math.Max(5, SettingsService.Settings.ClipboardClearSeconds);
        ShowToast($"{label}已复制 · 安全剪贴板将在 {seconds} 秒后自动清空");

        _clipboardClearCts?.Cancel();
        _clipboardClearCts = new CancellationTokenSource();
        var token = _clipboardClearCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (GetClipboardTextAsync is not null)
                    {
                        var cur = await GetClipboardTextAsync();
                        if (cur == _lastCopiedSecret)
                        {
                            await CopyToClipboardAsync(string.Empty);
                            ShowToast("安全剪贴板已自动清空");
                        }
                    }
                });
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    public void ShowToast(string message)
    {
        ToastMessage = message;
        IsToastVisible = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ToastMessage == message) IsToastVisible = false;
            });
        });
    }

    private static void UpdateObservable(ObservableCollection<EntryItemViewModel> target, List<KdbxEntry> items)
    {
        target.Clear();
        foreach (var e in items)
            target.Add(new EntryItemViewModel(e));
    }
}
