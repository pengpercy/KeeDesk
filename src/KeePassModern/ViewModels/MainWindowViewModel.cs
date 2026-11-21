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
    Tag
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public AppSettingsService SettingsService { get; }
    private KdbxDatabase? _database;
    private CompositeKey? _masterKey;
    private readonly DispatcherTimer _tickTimer;
    private CancellationTokenSource? _clipboardClearCts;
    private string? _lastCopiedSecret;

    public bool IsMacOS { get; } = OperatingSystem.IsMacOS();
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
    [ObservableProperty] private EntryItemViewModel? _selectedEntry;
    [ObservableProperty] private GroupItemViewModel? _selectedGroup;
    private string? _selectedTag;

    // Security Audit Dashboard
    [ObservableProperty] private int _auditScore = 100;
    public ObservableCollection<EntryItemViewModel> AuditWeakEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditReusedEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditExpiringEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditNo2FaEntries { get; } = new();

    // Toast notification
    [ObservableProperty] private string _toastMessage = string.Empty;
    [ObservableProperty] private bool _isToastVisible;

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

    private void OnSecondTick()
    {
        if (!IsUnlocked) return;
        foreach (var item in FilteredEntries)
        {
            if (item.HasTotp) item.RefreshTotp();
        }
        SelectedEntry?.RefreshTotp();
    }

    partial void OnUnlockVaultPathChanged(string value) => OnPropertyChanged(nameof(UnlockVaultHeading));
    partial void OnUnlockKeyFilePathChanged(string value) => OnPropertyChanged(nameof(UnlockKeyFileDisplay));

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

        try
        {
            // 后台线程执行 Argon2id / AES-KDF 解密，保持 60fps 动画完全流畅不卡顿
            var (db, key) = await Task.Run(() =>
            {
                var k = CompositeKey.FromPasswordAndKeyFile(pwd, keyFile);
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
            // Add a helpful sample entry so users immediately experience all Fluent 2 detail features
            var sample = new KdbxEntry
            {
                Title = "GitHub 企业版",
                UserName = "admin@company.com",
                Password = PasswordGenerator.GenerateFromCharset(new PasswordGeneratorOptions { Length = 22 }),
                Url = "https://github.com/login",
                Notes = "欢迎使用 KeePassModern！支持 KDBX 4.1 内存保护、TOTP 动态验证码与三档界面密度切换。",
                IsFavorite = true
            };
            sample.Tags.Add("工作");
            sample.Tags.Add("代码");
            sample.SetString(KdbxConstants.FieldOtp, "otpauth://totp/GitHub:admin?secret=JBSWY3DPEHPK3PXP&issuer=GitHub", true);
            db.RootGroup.Groups[0].AddEntry(sample);

            var key = CompositeKey.FromPasswordAndKeyFile(UnlockMasterPassword, UnlockKeyFilePath);
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

        // 对齐设计稿画板 02 / 12 的子分组结构
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

        ActivateDatabase(db, CompositeKey.FromPassword("demo"));
        ShowToast("已进入演示密码库（已载入全部 6 条示例密码）");
    }

    private void ActivateDatabase(KdbxDatabase db, CompositeKey key)
    {
        _database?.Dispose();
        _masterKey?.Dispose();
        _database = db;
        _masterKey = key;
        VaultDisplayName = string.IsNullOrWhiteSpace(db.FilePath) ? db.Meta.DatabaseName : Path.GetFileName(db.FilePath);
        IsUnlocked = true;
        ActiveSection = ActiveSection.AllEntries;
        IsAuditViewActive = false;
        RebuildWorkspace();
    }

    [RelayCommand]
    private void LockVault()
    {
        if (_database is not null && _database.IsModified && !string.IsNullOrEmpty(_database.FilePath) && _masterKey is not null)
        {
            try { KdbxFormat.Save(_database, _database.FilePath, _masterKey); } catch { }
        }

        _database?.Dispose();
        _masterKey?.Dispose();
        _database = null;
        _masterKey = null;
        FilteredEntries.Clear();
        RootGroups.Clear();
        AllTags.Clear();
        SelectedEntry = null;
        IsUnlocked = false;
        IsModified = false;
        ShowToast("密码库已锁定，内存敏感数据已清零");
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
        DensityService.Apply(next);
        SettingsService.Settings.UiDensity = next.ToString();
        SettingsService.Save();
        CurrentDensityLabel = next switch
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
            _ => _database.EnumerateAllEntries(false)
        };

        CurrentFilterTitle = ActiveSection switch
        {
            ActiveSection.Favorites => "收藏夹",
            ActiveSection.TotpOnly => "动态验证码 (2FA)",
            ActiveSection.RecycleBin => "回收站",
            ActiveSection.Group when SelectedGroup is not null => SelectedGroup.Name,
            ActiveSection.Tag => $"#{_selectedTag}",
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
    private async Task EditSelectedEntryAsync()
    {
        if (_database is null || SelectedEntry is null || OpenEntryEditorAsync is null) return;
        var targetGroup = SelectedEntry.Model.Parent ?? _database.RootGroup;
        if (await OpenEntryEditorAsync(SelectedEntry.Model, targetGroup))
        {
            _database.IsModified = true;
            SaveVault();
            RebuildWorkspace();
        }
    }

    [RelayCommand]
    private void ToggleFavoriteSelected()
    {
        if (_database is null || SelectedEntry is null) return;
        SelectedEntry.Model.IsFavorite = !SelectedEntry.Model.IsFavorite;
        SelectedEntry.Model.LastModificationTime = DateTimeOffset.UtcNow;
        _database.IsModified = true;
        RebuildWorkspace();
    }

    [RelayCommand]
    private void DeleteSelectedEntry()
    {
        if (_database is null || SelectedEntry is null) return;
        _database.MoveEntryToRecycleBin(SelectedEntry.Model);
        RebuildWorkspace();
        ShowToast(ActiveSection == ActiveSection.RecycleBin ? "条目已永久删除" : "条目已移入回收站");
    }

    [RelayCommand]
    private void RestoreSelectedEntry()
    {
        if (_database is null || SelectedEntry is null) return;
        if (_database.RestoreEntryFromRecycleBin(SelectedEntry.Model))
        {
            RebuildWorkspace();
            ShowToast("已恢复到原分组");
        }
    }

    [RelayCommand]
    private void ToggleRevealPassword()
    {
        if (SelectedEntry is not null)
            SelectedEntry.IsPasswordRevealed = !SelectedEntry.IsPasswordRevealed;
    }

    [RelayCommand]
    private async Task CopyUserNameAsync()
    {
        if (SelectedEntry is null) return;
        await CopySecretAsync(SelectedEntry.UserName, "用户名");
    }

    [RelayCommand]
    private async Task CopyPasswordAsync()
    {
        if (SelectedEntry is null) return;
        var resolved = FieldReferenceResolver.Resolve(SelectedEntry.Password, SelectedEntry.Model, _database);
        await CopySecretAsync(resolved, "密码");
    }

    [RelayCommand]
    private async Task CopyTotpAsync()
    {
        if (SelectedEntry is null || !SelectedEntry.HasTotp) return;
        await CopySecretAsync(SelectedEntry.TotpRaw, "动态验证码");
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
