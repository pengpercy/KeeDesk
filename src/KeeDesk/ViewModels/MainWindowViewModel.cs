using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using KeeDesk.Helpers;
using KeeDesk.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.ViewModels;

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

public sealed record SimilarPasswordPairViewModel(EntryItemViewModel First, EntryItemViewModel Second, double Similarity);

public enum ToastSeverity { Information, Success, Error }

/// <summary>
/// 表示一个已解锁的密码库标签页会话，支持同时打开多个 .kdbx 自由切换。
/// </summary>
public sealed partial class VaultTabSession : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public KdbxDatabase Database { get; internal set; }
    public CompositeKey MasterKey { get; set; }
    public string? KeyFilePath { get; set; }
    public bool HasPendingMasterKeyChange { get; set; }
    public bool HasFileConflict { get; set; }
    public RemoteVaultClient? RemoteVault { get; set; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private bool _isActive;

    public ActiveSection SavedSection { get; set; } = ActiveSection.AllEntries;
    public Guid? SavedGroupUuid { get; set; }
    public Guid? SavedEntryUuid { get; set; }
    public bool SavedSortByModified { get; set; }

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
    private VaultTabSession? _activeSession;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? _clipboardClearCts;
    private CancellationTokenSource? _autoTypeCancellation;
    private string? _lastCopiedSecret;

    public ObservableCollection<VaultTabSession> OpenVaults { get; } = new();
    public bool HasMultipleVaults => OpenVaults.Count > 1;

    public bool IsMacOS { get; } = OperatingSystem.IsMacOS();
    public bool IsWindows { get; } = OperatingSystem.IsWindows();
    public bool UsesLeftWindowChrome => DesktopWindowChrome.ButtonsOnLeft;
    public bool IsTouchIdSupported { get; } = MacTouchIdService.IsSupported();
    public string ShortcutModifier => IsMacOS ? "⌘" : "Ctrl+";
    public string SearchShortcutHint => ShortcutModifier + "K";

    // 导航选中状态，用于驱动左侧 3px 品牌色指示条
    public bool IsAllEntriesSelected => ActiveSection == ActiveSection.AllEntries;
    public bool IsFavoritesSelected => ActiveSection == ActiveSection.Favorites;
    public bool IsTotpSelected => ActiveSection == ActiveSection.TotpOnly;
    public bool IsAuditSelected => ActiveSection == ActiveSection.SecurityAudit;
    public bool IsRecycleBinSelected => ActiveSection == ActiveSection.RecycleBin;

    [ObservableProperty] private string _currentDensityLabel = L("Str_Standard");
    [ObservableProperty] private bool _sortByModifiedDesc;
    public string SortButtonLabel => SortByModifiedDesc ? L("Str_LastModified") : L("Str_ByName");

    [ObservableProperty] private double _leftCaptionPadding = 76;
    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private bool _isUnlocking;
    [ObservableProperty] private bool _isAuditViewActive;
    [ObservableProperty] private bool _isCheckingForUpdates;

    // 供视图订阅的丝滑动画事件：解锁失败抖动、解锁成功转场、聚焦搜索框
    public event Action? UnlockFailedShakeRequested;
    public event Action? FocusSearchRequested;

    // Unlock / Welcome view properties
    [ObservableProperty] private string _unlockVaultPath = string.Empty;
    [ObservableProperty] private string _unlockKeyFilePath = string.Empty;
    [ObservableProperty] private string _unlockMasterPassword = string.Empty;
    [ObservableProperty] private bool _unlockUseWindowsUserAccount;
    [ObservableProperty] private bool _rememberForTouchId;
    [ObservableProperty] private string _unlockError = string.Empty;

    public bool CanUnlockWithTouchId =>
        IsTouchIdSupported &&
        !string.IsNullOrWhiteSpace(UnlockVaultPath) &&
        MacTouchIdService.HasSavedPassword(UnlockVaultPath);
    public bool CanEnableTouchId => IsTouchIdSupported && !CanUnlockWithTouchId;

    [ObservableProperty] private string _newVaultName = L("Str_PersonalVault");
    [ObservableProperty] private RecentVaultItem? _selectedRecentVault;
    public ObservableCollection<RecentVaultItem> RecentVaults { get; } = new();
    public bool HasRecentVaults => RecentVaults.Count != 0;

    public string UnlockVaultFileName => Path.GetFileName(UnlockVaultPath);

    public string UnlockVaultHeading => string.IsNullOrWhiteSpace(UnlockVaultPath)
        ? L("Str_WelcomeToKeeDesk")
        : L("Str_UnlockFormat", SelectedRecentVault?.DisplayName ?? Path.GetFileNameWithoutExtension(UnlockVaultPath));

    public string UnlockKeyFileDisplay => string.IsNullOrWhiteSpace(UnlockKeyFilePath)
        ? L("Str_NoKeyFileSelected")
        : Path.GetFileName(UnlockKeyFilePath);

    // Workspace properties
    [ObservableProperty] private string _vaultDisplayName = "KeeDesk";
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public string VaultSaveStatus => _activeSession?.HasFileConflict == true
        ? L("Str_VaultChangedOnDiskStatus")
        : IsModified ? L("Str_UnsavedChanges") : L("Str_AllChangesSaved");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    private string _searchQuery = string.Empty;
    [ObservableProperty] private string _currentFilterTitle = L("Str_AllEntries3");
    [ObservableProperty] private ActiveSection _activeSection = ActiveSection.AllEntries;
    [ObservableProperty] private int _allCount;
    [ObservableProperty] private int _favoritesCount;
    [ObservableProperty] private int _totpCount;
    [ObservableProperty] private int _auditIssueCount;
    [ObservableProperty] private int _auditAffectedCount;
    [ObservableProperty] private string _auditCheckedDisplay = string.Empty;
    [ObservableProperty] private int _recycleBinCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VaultSaveStatus))]
    private bool _isModified;

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
    private string _customFilterTitleKey = "Str_SearchResults";
    private string? _advancedSearchQuery;
    private DateTime _auditCheckedAt;
    private string _defaultVaultName = L("Str_PersonalVault");
    private List<KdbxEntry>? _customFilterEntries;

    // Security Audit Dashboard
    [ObservableProperty] private int _auditScore = 100;
    public ObservableCollection<EntryItemViewModel> AuditWeakEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditReusedEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditExpiringEntries { get; } = new();
    public ObservableCollection<EntryItemViewModel> AuditNo2FaEntries { get; } = new();
    public ObservableCollection<SimilarPasswordPairViewModel> AuditSimilarPasswords { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSimilarPasswordScanEmpty))]
    private bool _hasSimilarPasswordScanResult;
    public bool IsSimilarPasswordScanEmpty => HasSimilarPasswordScanResult && AuditSimilarPasswords.Count == 0;

    // Toast notification
    [ObservableProperty] private string _toastMessage = string.Empty;
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsToastSuccess), nameof(IsToastError))]
    private ToastSeverity _toastKind;
    public bool IsToastSuccess => ToastKind == ToastSeverity.Success;
    public bool IsToastError => ToastKind == ToastSeverity.Error;

    // 详情页复制按钮绿色对钩短暂反馈状态
    [ObservableProperty] private bool _isUserNameCopied;
    [ObservableProperty] private bool _isPasswordCopied;
    [ObservableProperty] private bool _isTotpCopied;

    // Callbacks wired by MainWindow for native dialogs/clipboard
    public Func<string, Task>? CopyToClipboardAsync { get; set; }
    public Func<Task<string?>>? GetClipboardTextAsync { get; set; }
    public Func<KdbxEntry?, KdbxGroup, bool, Task<bool>>? OpenEntryEditorAsync { get; set; }
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
    public Func<KdbxDatabase, Task<(string Query, List<KdbxEntry> Entries)?>>? OpenAdvancedSearchAsync { get; set; }
    public Func<string, List<AutoTypeMatchResult>, CancellationToken, Task<AutoTypeMatchResult?>>? OpenAutoTypePickerAsync { get; set; }
    public Func<AutoTypePickCharactersStep, CancellationToken, Task<IReadOnlyList<int>?>>? OpenAutoTypeCharactersAsync { get; set; }
    public Func<DesktopShortcut, bool>? RegisterAutoTypeShortcut { get; set; }
    public bool IsAutoTypeShortcutRegistered { get; set; }
    public Action<string>? OpenUrlExternal { get; set; }
    public Action? MinimizeWindowAction { get; set; }

    public bool HasRecycleBinItems => RecycleBinCount > 0;
    public bool HasUnlockKeyFile => !string.IsNullOrWhiteSpace(UnlockKeyFilePath);

    public MainWindowViewModel(AppSettingsService settingsService)
    {
        SettingsService = settingsService;
        UpdateDensityLabel();
        RefreshRecentVaults();

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => OnSecondTick();
        _tickTimer.Start();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };
    }

    internal void RefreshLanguage()
    {
        if (NewVaultName == _defaultVaultName)
            NewVaultName = L("Str_PersonalVault");
        _defaultVaultName = L("Str_PersonalVault");
        UpdateDensityLabel();
        OnPropertyChanged(nameof(CurrentThemeLabel));
        OnPropertyChanged(nameof(SortButtonLabel));
        OnPropertyChanged(nameof(UnlockVaultHeading));
        OnPropertyChanged(nameof(UnlockKeyFileDisplay));
        OnPropertyChanged(nameof(VaultSaveStatus));
        CurrentFilterTitle = GetFilterTitle();
        if (_database is not null)
            AuditCheckedDisplay = L("Str_CheckedEntriesFormat", AllCount, _auditCheckedAt);
        foreach (var entry in FilteredEntries.Concat(AuditWeakEntries).Concat(AuditReusedEntries)
                     .Concat(AuditExpiringEntries).Concat(AuditNo2FaEntries)
                     .Concat(AuditSimilarPasswords.SelectMany(pair => new[] { pair.First, pair.Second })).Distinct())
            entry.Refresh();
        UnlockError = string.Empty;
        IsToastVisible = false;
    }

    internal void UpdateDensityLabel() => CurrentDensityLabel = DensityService.Current switch
    {
        UiDensity.Compact => L("Str_Compact"),
        UiDensity.Comfortable => L("Str_Spacious"),
        _ => L("Str_Standard")
    };

    public KdbxDatabase? CurrentDatabase => _database;

    public void UpdateMacTitleBarInsets(double trafficLightsRight)
    {
        LeftCaptionPadding = Math.Max(68, trafficLightsRight + 12);
    }

    public void RecordUserActivity() => _lastActivityUtc = DateTimeOffset.UtcNow;

    private void OnSecondTick()
    {
        if (!IsUnlocked) return;

        // 空闲超时自动锁定（支持在设置中开关启用状态与调整分钟时长）
        if (SettingsService.Settings.AutoLockIdleEnabled)
        {
            int idleMins = SettingsService.Settings.AutoLockIdleMinutes;
            if (idleMins > 0 && (DateTimeOffset.UtcNow - _lastActivityUtc).TotalMinutes >= idleMins)
            {
                LockAllVaults(L("Str_VaultsLockedAfterMinutesOfInactivityFormat", idleMins));
                return;
            }
        }

        foreach (var item in FilteredEntries)
        {
            if (item.HasTotp && item != SelectedEntry) item.RefreshTotp();
        }
        SelectedEntry?.RefreshTotp();
    }

    partial void OnUnlockVaultPathChanged(string value)
    {
        OnPropertyChanged(nameof(UnlockVaultHeading));
        OnPropertyChanged(nameof(UnlockVaultFileName));
        OnPropertyChanged(nameof(CanUnlockWithTouchId));
        OnPropertyChanged(nameof(CanEnableTouchId));
    }
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
        OnPropertyChanged(nameof(HasRecentVaults));
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
        ShowToast(L("Str_RemovedFromRecentsTheLocalFileIsFormat", item.DisplayName));
    }

    [RelayCommand]
    private void ClearRecentVaults()
    {
        SettingsService.ClearRecentVaults();
        UnlockVaultPath = string.Empty;
        UnlockKeyFilePath = string.Empty;
        RefreshRecentVaults();
        ShowToast(L("Str_ClearedRecentVaults"));
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
        var suggested = string.IsNullOrWhiteSpace(NewVaultName) ? "KeeDesk.keyx" : $"{NewVaultName}.keyx";
        var path = await PickSaveFileAsync(suggested, "keyx");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var xml = CompositeKey.GenerateXmlKeyFileV2();
            await File.WriteAllTextAsync(path, xml);
            if (!IsUnlocked)
                UnlockKeyFilePath = path;
            ShowToast(L("Str_GeneratedXMLV20KeyFileFormat", Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            ShowToast(L("Str_CouldnTGenerateTheKeyFileFormat", ex.Message), ToastSeverity.Error);
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
            ShowToast(L("Str_LoadedVaultEnterTheMasterPasswordToFormat", Path.GetFileName(kdbx)));
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
                ShowToast(L("Str_SelectedKeyFileFormat", Path.GetFileName(keyFile)));
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
            UnlockError = L("Str_SelectAValidKdbxVaultFile");
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

            ActivateDatabase(db, key, keyFile);
            SettingsService.AddRecentVault(db.FilePath!, string.IsNullOrWhiteSpace(keyFile) ? null : keyFile, db.Meta.DatabaseName);
            RefreshRecentVaults();

            if (RememberForTouchId && IsTouchIdSupported && !string.IsNullOrEmpty(pwd))
            {
                MacTouchIdService.SavePasswordToKeychain(vaultPath, pwd);
                OnPropertyChanged(nameof(CanUnlockWithTouchId));
        OnPropertyChanged(nameof(CanEnableTouchId));
            }
            UnlockMasterPassword = string.Empty;
        }
        catch (Exception ex)
        {
            UnlockError = L("Str_CouldnTUnlockUsingTheMasterPasswordFormat", ex.Message);
            UnlockFailedShakeRequested?.Invoke();
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    [RelayCommand]
    private async Task UnlockWithTouchIdAsync()
    {
        if (IsUnlocking || !CanUnlockWithTouchId) return;
        UnlockError = string.Empty;

        var ok = await MacTouchIdService.AuthenticateAsync(L("Str_UnlockVaultFormat", Path.GetFileName(UnlockVaultPath)));
        if (!ok)
        {
            UnlockError = L("Str_TouchIDAuthenticationFailedOrWasCanceled");
            return;
        }

        var savedPwd = MacTouchIdService.TryLoadPasswordFromKeychain(UnlockVaultPath);
        if (string.IsNullOrEmpty(savedPwd))
        {
            UnlockError = L("Str_CouldnTReadTheMasterPasswordFrom");
            OnPropertyChanged(nameof(CanUnlockWithTouchId));
        OnPropertyChanged(nameof(CanEnableTouchId));
            return;
        }

        UnlockMasterPassword = savedPwd;
        await UnlockVaultAsync();
    }

    [RelayCommand]
    private async Task CreateNewVaultAsync()
    {
        UnlockError = string.Empty;
        if (string.IsNullOrWhiteSpace(UnlockMasterPassword))
        {
            UnlockError = L("Str_EnterAMasterPasswordForTheNew");
            return;
        }

        string? savePath = PickSaveFileAsync is not null
            ? await PickSaveFileAsync($"{NewVaultName}.kdbx", "kdbx")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"{NewVaultName}.kdbx");

        if (string.IsNullOrWhiteSpace(savePath)) return;

        try
        {
            var db = KdbxDatabase.CreateDefault(NewVaultName);

            var key = CompositeKey.FromPasswordAndKeyFile(UnlockMasterPassword, UnlockKeyFilePath, UnlockUseWindowsUserAccount && IsWindows);
            KdbxFormat.Save(db, savePath, key);
            ActivateDatabase(db, key, UnlockKeyFilePath);
            SettingsService.AddRecentVault(db.FilePath!, string.IsNullOrWhiteSpace(UnlockKeyFilePath) ? null : UnlockKeyFilePath, db.Meta.DatabaseName);
            RefreshRecentVaults();
            UnlockMasterPassword = string.Empty;
            ShowToast(L("Str_CreatedAndSavedEncryptedVaultFormat", Path.GetFileName(savePath)));
        }
        catch (Exception ex)
        {
            UnlockError = L("Str_CouldnTCreateTheVaultFormat", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenDemoVault()
    {
        var personal = DemoVaultService.Create();
        var work = DemoVaultService.CreateWorkVault(personal);
        ActivateDatabase(personal, CompositeKey.FromPassword("demo"), sortByModified: true);
        OpenVaults.Add(new VaultTabSession(work, CompositeKey.FromPassword("demo"), work.Meta.DatabaseName)
        {
            SavedSortByModified = true,
        });
        OnPropertyChanged(nameof(HasMultipleVaults));
    }

    private void ActivateDatabase(KdbxDatabase db, CompositeKey key, string? keyFilePath = null, bool? sortByModified = null)
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
        var session = new VaultTabSession(db, key, display)
        {
            KeyFilePath = string.IsNullOrWhiteSpace(keyFilePath) ? null : keyFilePath,
            SavedSortByModified = sortByModified ?? SortByModifiedDesc
        };
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
            _activeSession.SavedSection = ActiveSection == ActiveSection.CustomFilter ? ActiveSection.AllEntries : ActiveSection;
            _activeSession.SavedGroupUuid = SelectedGroup?.Model.Uuid;
            _activeSession.SavedEntryUuid = SelectedEntry?.Model.Uuid;
            _activeSession.SavedSortByModified = SortByModifiedDesc;
            _activeSession.IsActive = false;
        }

        _activeSession = session;
        session.IsActive = true;
        _database = session.Database;
        _customFilterEntries = null;
        _advancedSearchQuery = null;
        VaultDisplayName = session.DisplayName;
        IsUnlocked = true;
        RecordUserActivity();

        ActiveSection = session.SavedSection;
        SortByModifiedDesc = session.SavedSortByModified;
        IsAuditViewActive = ActiveSection == ActiveSection.SecurityAudit;
        RebuildWorkspace();
        OnPropertyChanged(nameof(VaultSaveStatus));

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

        if (!TrySavePendingVault(target))
        {
            SwitchVaultTab(target);
            return;
        }

        _autoTypeCancellation?.Cancel();
        _remoteVaultOperation?.Cancel();
        var closedName = target.DisplayName;
        target.RemoteVault?.Dispose();
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
            ShowToast(L("Str_ClosedAndLockedVaultFormat", closedName));
        }
        else
        {
            _activeSession = null;
            _database = null;
            ClearSensitiveSearchState();
            FilteredEntries.Clear();
            RootGroups.Clear();
            AllTags.Clear();
            SelectedEntry = null;
            IsUnlocked = false;
            IsModified = false;
            ShowToast(L("Str_ClosedAndLockedVaultFormat", closedName));
        }
    }

    [RelayCommand]
    private async Task OpenAnotherVaultAsync()
    {
        if (PickOpenFileAsync is null) return;
        await OpenVaultPathAsync(await PickOpenFileAsync("kdbx"), "Str_OpenAnotherVault2");
    }

    public Func<bool, Task<string?>>? PickCloudVaultFileAsync { get; set; }

    [RelayCommand]
    private async Task OpenCloudVaultAsync()
    {
        if (PickCloudVaultFileAsync is null) return;
        await OpenVaultPathAsync(await PickCloudVaultFileAsync(false), "Str_OpenCloudVault");
    }

    private async Task OpenVaultPathAsync(string? path, string heading)
    {
        if (string.IsNullOrWhiteSpace(path) || OpenVaultUnlockPromptAsync is null) return;

        var existing = OpenVaults.FirstOrDefault(s =>
            string.Equals(s.Database.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SwitchVaultTab(existing);
            ShowToast(L("Str_SwitchedToTheOpenVaultFormat", existing.DisplayName));
            return;
        }

        var res = await OpenVaultUnlockPromptAsync(path, L(heading), L("Str_UnlockAndOpen"));
        if (res is null) return;

        ActivateDatabase(res.Value.Database, res.Value.MasterKey, res.Value.KeyFilePath);
        SettingsService.AddRecentVault(res.Value.Database.FilePath!, string.IsNullOrWhiteSpace(res.Value.KeyFilePath) ? null : res.Value.KeyFilePath, res.Value.Database.Meta.DatabaseName);
        RefreshRecentVaults();
        ShowToast(L("Str_OpenedInANewTabFormat", Path.GetFileName(path)));
    }

    [RelayCommand]
    private async Task SaveVaultAsAsync()
    {
        var session = _activeSession;
        if (session is null || PickSaveFileAsync is null) return;
        var db = session.Database;
        var suggested = string.IsNullOrWhiteSpace(db.FilePath)
            ? $"{db.Meta.DatabaseName}.kdbx"
            : Path.GetFileName(db.FilePath);
        var path = await PickSaveFileAsync(suggested, "kdbx");
        if (string.IsNullOrWhiteSpace(path) || session != _activeSession) return;

        try
        {
            KdbxFormat.Save(db, path, session.MasterKey);
            session.RemoteVault?.Dispose();
            session.RemoteVault = null;
            session.HasFileConflict = false;
            session.HasPendingMasterKeyChange = false;
            IsModified = false;
            OnPropertyChanged(nameof(VaultSaveStatus));
            VaultDisplayName = Path.GetFileName(path);
            session.DisplayName = VaultDisplayName;
            SettingsService.AddRecentVault(db.FilePath!, session.KeyFilePath, db.Meta.DatabaseName);
            RefreshRecentVaults();
            ShowToast(L("Str_SavedAsFormat", Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            ShowToast(L("Str_CouldnTSaveACopyFormat", DescribeSaveError(ex)), ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private void LockVault() => LockAllVaults();

    internal void LockAllVaults(string? automaticReason = null)
    {
        _autoTypeCancellation?.Cancel();
        _remoteVaultOperation?.Cancel();
        int recovered = 0;
        int failed = 0;
        foreach (var session in OpenVaults)
        {
            if (!TrySavePendingVault(session, reportError: false))
            {
                if (TrySaveRecoveryCopy(session))
                    recovered++;
                else
                    failed++;
            }
            session.Database.Dispose();
            session.MasterKey.Dispose();
            session.RemoteVault?.Dispose();
        }
        OpenVaults.Clear();
        OnPropertyChanged(nameof(HasMultipleVaults));
        _activeSession = null;
        _database = null;
        ClearSensitiveSearchState();
        FilteredEntries.Clear();
        RootGroups.Clear();
        AllTags.Clear();
        SelectedEntry = null;
        IsUnlocked = false;
        IsModified = false;
        if (recovered != 0)
            RefreshRecentVaults();
        string message = failed > 0
            ? L("Str_LockedWithRecoveryFailureFormat", failed, recovered)
            : recovered > 0
                ? L("Str_LockedWithRecoveryCopiesFormat", recovered)
                : automaticReason ?? L("Str_AllVaultsLockedSensitiveDataClearedFrom");
        ShowToast(message, failed > 0 ? ToastSeverity.Error : ToastSeverity.Information);
        if (automaticReason is not null || recovered > 0 || failed > 0)
            NativeNotificationService.Send("KeeDesk", message);
    }

    internal bool TrySaveBeforeClosing()
    {
        foreach (var session in OpenVaults)
        {
            if (!TrySavePendingVault(session))
            {
                SwitchVaultTab(session);
                return false;
            }
        }
        return true;
    }

    private bool TrySavePendingVault(VaultTabSession session, bool reportError = true)
    {
        if (!session.Database.IsModified || string.IsNullOrEmpty(session.Database.FilePath))
            return true;
        return TrySaveOpenVault(session, reportError);
    }

    private bool TrySaveRecoveryCopy(VaultTabSession session)
    {
        var db = session.Database;
        try
        {
            var folder = SettingsService.RecoveryDirectory;
            Directory.CreateDirectory(folder);
            var name = Path.GetFileNameWithoutExtension(db.FilePath);
            var path = Path.Combine(folder, $"{name}-recovery-{Guid.NewGuid():N}.kdbx");
            KdbxFormat.Save(db, path, session.MasterKey);
            SettingsService.AddRecentVault(path, session.KeyFilePath, Path.GetFileName(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveVault() => TrySaveActiveVault();

    [RelayCommand]
    private async Task SaveVaultAsync()
    {
        if (!TrySaveActiveVault()) return;
        if (_activeSession is { RemoteVault: { } remote } session)
            await SynchronizeRemoteVaultAsync(session, remote);
    }

    private bool TrySaveActiveVault()
    {
        if (_activeSession is not { } session) return false;
        if (string.IsNullOrWhiteSpace(session.Database.FilePath))
        {
            ShowToast(L("Str_ToSaveChangesToTheDemoCreate"));
            return false;
        }

        if (!TrySaveOpenVault(session)) return false;
        ShowToast(L(session.RemoteVault is null ? "Str_VaultSavedSecurelyKDBX4" : "Str_RemoteVaultSavedLocally"),
            ToastSeverity.Success);
        return true;
    }

    private bool TrySaveOpenVault(VaultTabSession session, bool reportError = true)
    {
        try
        {
            KdbxFormat.Save(session.Database, session.Database.FilePath!, session.MasterKey);
            session.HasFileConflict = false;
            if (session.HasPendingMasterKeyChange)
            {
                session.HasPendingMasterKeyChange = false;
                SettingsService.AddRecentVault(session.Database.FilePath!, session.KeyFilePath, session.Database.Meta.DatabaseName);
                RefreshRecentVaults();
            }
            return true;
        }
        catch (Exception ex)
        {
            session.HasFileConflict = ex is KdbxFileChangedException;
            if (reportError)
                ShowToast(L("Str_CouldnTSaveChangesFormat", DescribeSaveError(ex)), ToastSeverity.Error);
            return false;
        }
        finally
        {
            if (_activeSession == session)
            {
                IsModified = session.Database.IsModified;
                OnPropertyChanged(nameof(VaultSaveStatus));
            }
        }
    }

    private static string DescribeSaveError(Exception ex) =>
        ex is KdbxFileChangedException ? L("Str_VaultChangedOnDisk") : ex.Message;

    public void RebuildWorkspace()
    {
        if (_database is null) return;
        ClearSimilarPasswordReport();

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
        AuditAffectedCount = audit.WeakPasswords.Concat(audit.ReusedPasswords).Concat(audit.ExpiredOrExpiringSoon)
            .Select(entry => entry.Uuid).Distinct().Count();
        _auditCheckedAt = DateTime.Now;
        AuditCheckedDisplay = L("Str_CheckedEntriesFormat", audit.TotalEntries, _auditCheckedAt);
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
        UpdateDensityLabel();
        ShowToast(L("Str_UIDensityFormat2", CurrentDensityLabel));
    }

    public string CurrentThemeLabel => SettingsService.Settings.ThemeMode switch
    {
        "Light" => L("Str_Light"),
        "Dark" => L("Str_Dark"),
        _ => L("Str_FollowSystem"),
    };

    [RelayCommand]
    private void SetThemeMode(string? mode)
    {
        var target = string.IsNullOrWhiteSpace(mode) ? "Default" : mode;
        SettingsService.Settings.ThemeMode = target;
        SettingsService.Save();
        ThemeAccentService.ApplyThemeVariant(target);
        ThemeAccentService.ApplyAccent(SettingsService.Settings.AccentMode, SettingsService.Settings.CustomAccentHex);
        OnPropertyChanged(nameof(CurrentThemeLabel));
        ShowToast(L("Str_AppThemeFormat2", CurrentThemeLabel));
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
        if (!string.IsNullOrEmpty(SearchQuery))
            SearchQuery = string.Empty;
        else
            RefreshFilteredEntries();
        SelectedEntry = FilteredEntries.FirstOrDefault(e => e.Model.Uuid == item.Model.Uuid);
    }

    [RelayCommand]
    private void RefreshAudit() => RebuildWorkspace();

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanSimilarPasswordsAsync(CancellationToken cancellationToken)
    {
        if (_database is not { } database) return;
        if (!IsAuditViewActive)
            SelectSection(nameof(ActiveSection.SecurityAudit));

        HasSimilarPasswordScanResult = false;
        AuditSimilarPasswords.Clear();
        try
        {
            var pairs = await SecurityAuditService.FindSimilarPasswordsAsync(database, cancellationToken);
            if (database != _database || cancellationToken.IsCancellationRequested) return;

            foreach (var pair in pairs)
                AuditSimilarPasswords.Add(new SimilarPasswordPairViewModel(
                    new EntryItemViewModel(pair.First, database), new EntryItemViewModel(pair.Second, database), pair.Similarity));
            HasSimilarPasswordScanResult = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void ClearSimilarPasswordReport()
    {
        ScanSimilarPasswordsCommand.Cancel();
        HasSimilarPasswordScanResult = false;
        AuditSimilarPasswords.Clear();
    }

    private void ClearSensitiveSearchState()
    {
        _customFilterEntries = null;
        _advancedSearchQuery = null;
        SearchQuery = string.Empty;
        CurrentFilterTitle = L("Str_AllEntries3");
        ClearSimilarPasswordReport();
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

        CurrentFilterTitle = GetFilterTitle();

        if (HasSearchQuery)
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
            FilteredEntries.Add(new EntryItemViewModel(e, _database));

        SelectedEntry = FilteredEntries.FirstOrDefault(x => x.Model.Uuid == prevSelectedUuid) ?? FilteredEntries.FirstOrDefault();
    }

    private string GetFilterTitle()
    {
        if (HasSearchQuery) return L("Str_SearchResults2");
        return ActiveSection switch
        {
            ActiveSection.Favorites => L("Str_Favorites2"),
            ActiveSection.TotpOnly => L("Str_VerificationCodes2FA"),
            ActiveSection.RecycleBin => L("Str_RecycleBin"),
            ActiveSection.Group when SelectedGroup is not null => SelectedGroup.Name,
            ActiveSection.Tag => $"#{_selectedTag}",
            ActiveSection.CustomFilter when _advancedSearchQuery is not null => L(_customFilterTitleKey, _advancedSearchQuery),
            ActiveSection.CustomFilter => L(_customFilterTitleKey),
            _ => L("Str_AllEntries")
        };
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
        if (await OpenEntryEditorAsync(null, targetGroup, false))
        {
            _database.IsModified = true;
            SaveVault();
            RebuildWorkspace();
        }
    }

    [RelayCommand]
    private async Task EditSelectedEntryAsync() => await EditEntryAsync(SelectedEntry);

    [RelayCommand]
    private Task EditEntryAsync(EntryItemViewModel? item) => OpenEntryAsync(item, showHistory: false);

    [RelayCommand]
    private Task ViewEntryHistoryAsync(EntryItemViewModel? item) => OpenEntryAsync(item, showHistory: true);

    private async Task OpenEntryAsync(EntryItemViewModel? item, bool showHistory)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null || OpenEntryEditorAsync is null) return;
        var targetGroup = target.Model.Parent ?? _database.RootGroup;
        if (await OpenEntryEditorAsync(target.Model, targetGroup, showHistory))
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
        ShowToast(L("Str_DuplicatedEntryFormat", clone.Title));
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
        ShowToast(wasInBin ? L("Str_EntryPermanentlyDeleted") : L("Str_EntryMovedToTheRecycleBin"));
    }

    [RelayCommand]
    private void PermanentlyDeleteEntry(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (_database is null || target is null) return;
        _database.DeleteEntryPermanently(target.Model);
        SaveVault();
        RebuildWorkspace();
        ShowToast(L("Str_EntryPermanentlyDeleted"));
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
            ShowToast(L("Str_EntryRestoredToItsOriginalGroup"));
        }
    }

    [RelayCommand]
    private void EmptyRecycleBin()
    {
        if (_database is null || RecycleBinCount == 0) return;
        _database.EmptyRecycleBin();
        SaveVault();
        RebuildWorkspace();
        ShowToast(L("Str_RecycleBinEmptied"));
    }

    // ==================== 分组管理（新建 / 新建子分组 / 重命名 / 删除） ====================
    [RelayCommand]
    private async Task AddRootGroupAsync()
    {
        if (_database is null || OpenGroupDialogAsync is null) return;
        var res = await OpenGroupDialogAsync(L("Str_NewRootGroup2"), string.Empty, string.Empty);
        if (res is null) return;

        var g = new KdbxGroup { Name = res.Value.Name, Notes = res.Value.Notes, IconId = 48 };
        _database.RootGroup.AddGroup(g);
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast(L("Str_CreatedGroupFormat", g.Name));
    }

    [RelayCommand]
    private async Task AddSubGroupAsync(GroupItemViewModel? parent)
    {
        if (_database is null || parent is null || OpenGroupDialogAsync is null) return;
        var res = await OpenGroupDialogAsync(L("Str_NewSubgroupInFormat", parent.Name), string.Empty, string.Empty);
        if (res is null) return;

        var g = new KdbxGroup { Name = res.Value.Name, Notes = res.Value.Notes, IconId = 48 };
        parent.Model.AddGroup(g);
        parent.Model.IsExpanded = true;
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast(L("Str_CreatedSubgroupFormat", g.Name));
    }

    [RelayCommand]
    private async Task RenameGroupAsync(GroupItemViewModel? group)
    {
        if (_database is null || group is null || OpenGroupDialogAsync is null) return;
        var res = await OpenGroupDialogAsync(L("Str_RenameGroup"), group.Model.Name, group.Model.Notes);
        if (res is null) return;

        group.Model.Name = res.Value.Name;
        group.Model.Notes = res.Value.Notes;
        group.Model.LastModificationTime = DateTimeOffset.UtcNow;
        _database.IsModified = true;
        SaveVault();
        RebuildWorkspace();
        ShowToast(L("Str_UpdatedGroupFormat", group.Model.Name));
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
        ShowToast(_database.Meta.RecycleBinEnabled ? L("Str_MovedGroupToTheRecycleBinFormat", name) : L("Str_DeletedGroupFormat", name));
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
        ShowToast(L("Str_SortedSubgroupsOfByNameFormat", group.Name));
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
            ShowToast(L("Str_MovedRecordsToGroupFormat", moved, dest.Name));
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
        ShowToast(targetFav ? L("Str_AddedRecordsToFavoritesFormat", _selectedEntries.Count) : L("Str_RemovedRecordsFromFavoritesFormat", _selectedEntries.Count));
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
        ShowToast(ActiveSection == ActiveSection.RecycleBin ? L("Str_PermanentlyDeletedRecordsFormat", count) : L("Str_MovedRecordsToTheRecycleBinFormat", count));
    }

    [RelayCommand]
    private async Task CopyUserNameAsync() => await CopyEntryUserNameAsync(SelectedEntry);

    [RelayCommand]
    private async Task CopyEntryUserNameAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null) return;
        var resolved = FieldReferenceResolver.Resolve(target.UserName, target.Model, _database);
        await CopySecretAsync(resolved, L("Str_UserName"));
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
        await CopySecretAsync(resolved, L("Str_Password"));
        if (target == SelectedEntry) FlashCopiedFlag(v => IsPasswordCopied = v);
    }

    [RelayCommand]
    private async Task CopyTotpAsync() => await CopyEntryTotpAsync(SelectedEntry);

    [RelayCommand]
    private async Task CopyEntryTotpAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null) return;
        target.RefreshTotp();
        if (!target.HasTotp) return;
        await CopySecretAsync(target.TotpRaw, L("Str_VerificationCode"));
        if (target == SelectedEntry) FlashCopiedFlag(v => IsTotpCopied = v);
    }

    [RelayCommand]
    private async Task CopyEntryUrlAsync(EntryItemViewModel? item)
    {
        var target = item ?? SelectedEntry;
        if (target is null || string.IsNullOrWhiteSpace(target.Url)) return;
        var resolved = FieldReferenceResolver.Resolve(target.Url, target.Model, _database);
        await CopySecretAsync(resolved, L("Str_URL"));
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
    private Task PerformAutoTypeAsync(EntryItemViewModel? item) => WithAutoTypeAsync(async cancellationToken =>
    {
        var entry = (item ?? SelectedEntry)?.Model;
        var vault = _database;
        if (entry is null || vault is null || vault.FindEntryByUuid(entry.Uuid) != entry) return;
        if (!AutoTypeMatcher.IsEnabled(entry) || vault.IsInRecycleBin(entry))
        {
            ShowToast(L("Str_AutoTypeIsDisabledForThisEntry"), ToastSeverity.Error);
            return;
        }

        MinimizeWindowAction?.Invoke();
        await Task.Delay(300, cancellationToken);
        using var target = AutoTypeTarget.CaptureManualTarget();
        if (!IsExternalAutoTypeTarget(target)) return;
        var match = AutoTypeMatcher.MatchSelectedEntry(vault, entry, target!.Title);
        if (match is not null)
            await ExecuteAutoTypeMatchAsync(match, target, cancellationToken);
    });

    [RelayCommand]
    private Task TriggerGlobalAutoTypeAsync() => WithAutoTypeAsync(async cancellationToken =>
    {
        using var target = AutoTypeTarget.Capture();
        if (!IsExternalAutoTypeTarget(target)) return;

        var vaults = OpenVaults.Select(session => session.Database).ToArray();
        var matches = AutoTypeMatcher.FindMatches(vaults, target!.Title);
        AutoTypeMatchResult? match;
        if (matches.Count == 1 && !matches[0].RequiresConfirmation)
            match = matches[0];
        else
        {
            if (matches.Count == 0)
                matches = AutoTypeMatcher.FindManualChoices(vaults);
            if (matches.Count == 0 || OpenAutoTypePickerAsync is null) return;
            match = await OpenAutoTypePickerAsync(target.Title, matches, cancellationToken);
        }

        if (match is not null)
            await ExecuteAutoTypeMatchAsync(match, target, cancellationToken);
    });

    private async Task WithAutoTypeAsync(Func<CancellationToken, Task> action)
    {
        if (!IsUnlocked || _autoTypeCancellation is not null) return;
        var unavailable = AutoTypeService.CheckAvailability();
        if (unavailable is not null)
        {
            ShowToast(unavailable, ToastSeverity.Error);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _autoTypeCancellation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { }
        finally { _autoTypeCancellation = null; }
    }

    private bool IsExternalAutoTypeTarget(AutoTypeTarget? target)
    {
        if (target is not null && (target.UsesWaylandKeyboard ||
            (target.ProcessId != Environment.ProcessId && target.Window != 0)))
            return true;
        ShowToast(L("Str_AutoTypeNoTargetWindow"), ToastSeverity.Error);
        return false;
    }

    private async Task ExecuteAutoTypeMatchAsync(AutoTypeMatchResult match, AutoTypeTarget target,
        CancellationToken cancellationToken)
    {
        if (match.Vault is not { } vault || !OpenVaults.Any(session => session.Database == vault) ||
            vault.FindEntryByUuid(match.Entry.Uuid) != match.Entry) return;
        var result = await AutoTypeService.ExecuteAsync(match, target, OpenAutoTypeCharactersAsync,
            MinimizeWindowAction, cancellationToken);
        if (!cancellationToken.IsCancellationRequested)
            ShowToast(result.Message, result.Success ? ToastSeverity.Information : ToastSeverity.Error);
    }

    [RelayCommand]
    private async Task CopyCustomFieldAsync(CustomFieldItem? field)
    {
        if (field is null || SelectedEntry is null) return;
        var resolved = FieldReferenceResolver.Resolve(field.RawValue, SelectedEntry.Model, _database);
        await CopySecretAsync(resolved, L("Str_FieldFormat", field.Key));
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
            ShowToast(L("Str_ExportedAttachmentFormat", Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            ShowToast(L("Str_CouldnTExportTheAttachmentFormat", ex.Message), ToastSeverity.Error);
        }
    }

    // ==================== 密码库设置 / 修改主密钥 / 合并同步 ====================
    [RelayCommand]
    private async Task OpenDatabaseSettingsAsyncCommand()
    {
        var session = _activeSession;
        if (session is null || OpenDatabaseSettingsAsync is null) return;
        var (saved, newKey, newKeyFile) = await OpenDatabaseSettingsAsync(session.Database);
        if (!saved) return;
        if (session != _activeSession)
        {
            newKey?.Dispose();
            return;
        }

        if (newKey is not null)
        {
            session.MasterKey.Dispose();
            session.MasterKey = newKey;
            session.KeyFilePath = newKeyFile;
            session.HasPendingMasterKeyChange = true;
        }

        VaultDisplayName = string.IsNullOrWhiteSpace(session.Database.FilePath)
            ? session.Database.Meta.DatabaseName
            : Path.GetFileName(session.Database.FilePath);
        session.DisplayName = VaultDisplayName;
        bool persisted = TrySaveActiveVault();
        RebuildWorkspace();
        if (persisted)
            ShowToast(newKey is not null ? L("Str_VaultSettingsAndMasterKeyUpdatedVault") : L("Str_VaultSettingsUpdatedAndSaved"));
    }

    [RelayCommand]
    private async Task MergeDatabaseAsync()
    {
        var current = _activeSession;
        if (current is null || PickOpenFileAsync is null || OpenVaultUnlockPromptAsync is null) return;
        var path = await PickOpenFileAsync("kdbx");
        if (string.IsNullOrWhiteSpace(path) || current != _activeSession) return;

        var res = await OpenVaultUnlockPromptAsync(path, L("Str_MergeSyncAnotherVault2"), L("Str_DecryptAndMerge"));
        if (res is null) return;

        using var sourceDb = res.Value.Database;
        using var sourceKey = res.Value.MasterKey;
        if (current != _activeSession) return;
        var summary = DatabaseMergeService.Merge(current.Database, sourceDb);
        bool persisted = TrySaveActiveVault();
        RebuildWorkspace();
        if (persisted)
            ShowToast(L("Str_MergeCompleteEntriesAddedUpdatedGroupsAddedFormat", summary.EntriesAdded, summary.EntriesUpdated, summary.GroupsAdded));
    }

    [RelayCommand]
    private Task SynchronizeVaultAsync() => SynchronizeVaultFromAsync(
        PickOpenFileAsync is { } pick ? () => pick("kdbx") : null, "Str_SyncWithLocalVault");

    [RelayCommand]
    private Task SynchronizeCloudVaultAsync() => SynchronizeVaultFromAsync(
        PickCloudVaultFileAsync is { } pick ? () => pick(true) : null, "Str_SyncCloudVault", reconcileSameFile: true);

    private async Task SynchronizeVaultFromAsync(Func<Task<string?>>? pick, string heading, bool reconcileSameFile = false)
    {
        var current = _activeSession;
        if (current is null || pick is null || OpenVaultUnlockPromptAsync is null) return;
        string? currentPath = current.Database.FilePath;
        if (currentPath is null)
        {
            ShowToast(L("Str_ToSaveChangesToTheDemoCreate"), ToastSeverity.Error);
            return;
        }

        var path = await pick();
        if (string.IsNullOrWhiteSpace(path) || current != _activeSession) return;
        try
        {
            if (DatabaseSynchronizationService.AreSameVaultFile(currentPath, path))
            {
                if (reconcileSameFile)
                {
                    var merged = DatabaseSynchronizationService.ReconcileFileChanges(current.Database, current.MasterKey);
                    if (merged is null)
                        ShowToast(L("Str_SyncVaultsAlreadyUpToDate"));
                    else
                    {
                        ApplySynchronizedVault(current, merged);
                        ShowToast(L("Str_CloudVaultFileReconciled"), ToastSeverity.Success);
                    }
                    return;
                }
                ShowToast(L("Str_SyncChooseAnotherVault"), ToastSeverity.Error);
                return;
            }

            var other = OpenVaults.FirstOrDefault(session => session != current &&
                session.Database.FilePath is { } file && DatabaseSynchronizationService.AreSameVaultFile(file, path));
            if (other is not null)
            {
                SynchronizeVaults(current, other.Database, other.MasterKey, other);
                return;
            }

            var result = await OpenVaultUnlockPromptAsync(path, L(heading), L("Str_UnlockAndSyncBothVaults"));
            if (result is null) return;
            using var otherDatabase = result.Value.Database;
            using var otherKey = result.Value.MasterKey;
            if (current != _activeSession || currentPath != current.Database.FilePath) return;
            SynchronizeVaults(current, otherDatabase, otherKey, null);
        }
        catch (Exception ex)
        {
            string message = ex switch
            {
                KdbxSyncRollbackException recovery => L("Str_SyncRecoveryFileFormat", recovery.RecoveryPath),
                KdbxFileChangedException => L("Str_SyncFilesChanged"),
                _ => L("Str_SyncFailedFormat", ex.Message)
            };
            ShowToast(message, ToastSeverity.Error);
        }
    }

    private void SynchronizeVaults(VaultTabSession current, KdbxDatabase other, CompositeKey key, VaultTabSession? otherSession)
    {
        var result = DatabaseSynchronizationService.Synchronize(current.Database, current.MasterKey, other, key);
        if (!result.Changed)
        {
            result.Current.Dispose();
            result.Other.Dispose();
            ShowToast(L("Str_SyncVaultsAlreadyUpToDate"));
            return;
        }

        ApplySynchronizedVault(current, result.Current);
        if (otherSession is not null)
            ApplySynchronizedVault(otherSession, result.Other);
        else
            result.Other.Dispose();
        RecordUserActivity();
        ShowToast(L("Str_SyncBothVaultsSaved"), ToastSeverity.Success);
    }

    private void ApplySynchronizedVault(VaultTabSession session, KdbxDatabase synchronized)
    {
        using var previous = session.Database;
        session.Database = synchronized;
        session.HasFileConflict = false;
        if (session.HasPendingMasterKeyChange)
        {
            session.HasPendingMasterKeyChange = false;
            SettingsService.AddRecentVault(synchronized.FilePath!, session.KeyFilePath, synchronized.Meta.DatabaseName);
            RefreshRecentVaults();
        }
        if (session != _activeSession) return;

        var selectedGroupId = SelectedGroup?.Model.Uuid;
        _database = synchronized;
        SelectedGroup = null;
        UpdateSelectedEntries(Array.Empty<EntryItemViewModel>());
        _customFilterEntries = null;
        _advancedSearchQuery = null;
        if (ActiveSection is ActiveSection.CustomFilter or ActiveSection.Group)
            ActiveSection = ActiveSection.AllEntries;
        RebuildWorkspace();
        if (selectedGroupId is { } id)
            SelectedGroup = FindGroupVm(RootGroups, id);
        OnPropertyChanged(nameof(VaultSaveStatus));
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
            ShowToast(L("Str_MovedToGroupFormat", target.Title, dest.Name));
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
            ShowToast(L("Str_MovedToGroupFormat", entry.Title, targetGroup.Name));
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
            ShowToast(L("Str_VaultMaintenanceCompletedAndChangesSaved"));
        }
    }

    // ==================== 高级查找 / 预置快捷筛选 / 快捷过期编辑 / KeePass XML ====================
    [RelayCommand]
    private async Task OpenAdvancedSearchAsyncCommand()
    {
        if (_database is not { } database || OpenAdvancedSearchAsync is null) return;
        if (await OpenAdvancedSearchAsync(database) is not { } result || _database != database) return;

        _customFilterEntries = result.Entries;
        _advancedSearchQuery = string.IsNullOrWhiteSpace(result.Query) ? null : result.Query;
        _customFilterTitleKey = _advancedSearchQuery is null ? "Str_AdvancedSearchResults" : "Str_SearchForFormat";
        ActiveSection = ActiveSection.CustomFilter;
        SelectedGroup = null;
        IsAuditViewActive = false;
        if (!string.IsNullOrEmpty(SearchQuery))
            SearchQuery = string.Empty;
        else
            RefreshFilteredEntries();
        ShowToast(L("Str_AdvancedSearchMatchedEntriesFormat", _customFilterEntries.Count));
    }

    internal bool TrySaveSearchProfiles(KdbxDatabase database, SearchProfile[] profiles)
    {
        if (_activeSession is not { } session || _database != database ||
            string.IsNullOrWhiteSpace(database.FilePath))
            return false;

        var saved = SearchProfileStore.TrySave(database, profiles, () => TrySaveOpenVault(session, reportError: false));
        IsModified = database.IsModified;
        return saved;
    }

    [RelayCommand]
    private void ShowQuickFilter(string? preset)
    {
        if (_database is null || string.IsNullOrWhiteSpace(preset)) return;

        _advancedSearchQuery = null;
        (_customFilterTitleKey, _customFilterEntries) = preset switch
        {
            "Expired" => ("Str_ExpiredEntries", EntrySearchService.FindExpired(_database)),
            "Expiring7" => ("Str_ExpiresWithin7Days", EntrySearchService.FindExpiringWithinDays(_database, 7)),
            "Expiring30" => ("Str_ExpiresWithin30Days", EntrySearchService.FindExpiringWithinDays(_database, 30)),
            "LastModified" => ("Str_RecentlyModifiedEntries", EntrySearchService.FindLastModified(_database, 30)),
            "Large" => ("Str_LargeEntriesAndAttachments", EntrySearchService.FindLargeEntries(_database, 30)),
            _ => ("Str_AllEntries3", _database.EnumerateAllEntries(false).ToList())
        };

        ActiveSection = ActiveSection.CustomFilter;
        SelectedGroup = null;
        IsAuditViewActive = false;
        RefreshFilteredEntries();
    }

    [RelayCommand]
    private void QuickExpireNow(EntryItemViewModel? item) => ApplyQuickExpiry(item, true, DateTimeOffset.UtcNow.AddMinutes(-1), L("Str_SetToExpireNow"));

    [RelayCommand]
    private void QuickExpireNever(EntryItemViewModel? item) => ApplyQuickExpiry(item, false, DateTimeOffset.UtcNow.AddYears(1), L("Str_SetToNeverExpire"));

    [RelayCommand]
    private void QuickExpireIn30Days(EntryItemViewModel? item) => ApplyQuickExpiry(item, true, DateTimeOffset.UtcNow.AddDays(30), L("Str_SetToExpireIn30Days"));

    [RelayCommand]
    private void QuickExpireIn90Days(EntryItemViewModel? item) => ApplyQuickExpiry(item, true, DateTimeOffset.UtcNow.AddDays(90), L("Str_SetToExpireIn90Days"));

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
        ShowToast(L("Str_ExportedKeePass2XXMLFormat", Path.GetFileName(path)));
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
            ShowToast(L("Str_KeePassXMLImportedEntriesAddedUpdatedFormat", summary.EntriesAdded, summary.EntriesUpdated));
        }
        catch (Exception ex)
        {
            ShowToast(L("Str_CouldnTImportXMLFormat", ex.Message), ToastSeverity.Error);
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
            await CopySecretAsync(generated, L("Str_NewlyGeneratedPassword"));
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
            ReleaseInfo? release;
            if (!silent) IsCheckingForUpdates = true;
            try
            {
                release = await updateService.CheckForUpdatesAsync(current);
            }
            finally
            {
                if (!silent) IsCheckingForUpdates = false;
            }

            if (release is not null && ShowUpdateWindowAsync is not null)
            {
                await ShowUpdateWindowAsync(release);
            }
            else if (!silent)
            {
                ShowToast(L("Str_YouReUpToDateVFormat", current));
            }
        }
        catch (Exception ex)
        {
            if (!silent)
                ShowToast(L("Str_CouldnTCheckForUpdatesFormat", ex.Message), ToastSeverity.Error);
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
        ShowToast(L("Str_ExportedCSVFormat", Path.GetFileName(path)));
    }

    public Func<string, Task<(CsvImportDocument Document, CsvImportField[] Mapping, bool HasHeader)?>>? OpenCsvMappingAsync { get; set; }
    public Func<Task<VaultPrintOptions?>>? OpenVaultPrintOptionsAsync { get; set; }
    public Func<string, Task<KdbxDatabase?>>? OpenLegacyKdbAsync { get; set; }

    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        if (_database is not { } vault || PickOpenFileAsync is null || OpenCsvMappingAsync is null) return;
        var path = await PickOpenFileAsync("csv");
        if (string.IsNullOrWhiteSpace(path) || _database != vault) return;
        try
        {
            var text = await File.ReadAllTextAsync(path, new System.Text.UTF8Encoding(false, true));
            if (_database != vault) return;
            var selection = await OpenCsvMappingAsync(text);
            if (selection is not { } format || _database != vault) return;
            int count = ImportExportService.ImportFromCsv(vault, format.Document, format.Mapping, format.HasHeader,
                untitled: index => L("Str_CsvPreviewEntryFormat", index));
            bool saved = count == 0 || TrySaveActiveVault();
            RebuildWorkspace();
            if (saved) ShowToast(L("Str_ImportedRecordsFromCSVFormat", count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            ShowToast(L("Str_CsvReadFailed"), ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task PrintVaultAsync()
    {
        if (_database is not { } vault || OpenVaultPrintOptionsAsync is null || PickSaveFileAsync is null) return;
        var options = await OpenVaultPrintOptionsAsync();
        if (options is null || _database != vault) return;
        var path = await PickSaveFileAsync($"{vault.Meta.DatabaseName}.html", "html");
        if (string.IsNullOrWhiteSpace(path) || _database != vault) return;

        try
        {
            var entries = options.CurrentSelection ? FilteredEntries.Select(entry => entry.Model) : vault.EnumerateAllEntries();
            string html = VaultPrintReport.ExportHtml(vault.Meta.DatabaseName, entries, options);
            await File.WriteAllTextAsync(path, html);
            OpenUrlExternal?.Invoke(new Uri(path).AbsoluteUri);
            ShowToast(L("Str_PrintReportSaved"), ToastSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException)
        {
            ShowToast(L("Str_PrintReportFailed"), ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ImportLegacyKdbAsync()
    {
        if (_database is not { } vault || PickOpenFileAsync is null || OpenLegacyKdbAsync is null) return;
        var path = await PickOpenFileAsync("kdb");
        if (string.IsNullOrWhiteSpace(path) || _database != vault) return;

        using var source = await OpenLegacyKdbAsync(path);
        if (source is null || _database != vault) return;
        var result = DatabaseMergeService.Merge(vault, source);
        bool saved = TrySaveActiveVault();
        RebuildWorkspace();
        if (saved) ShowToast(L("Str_ImportedLegacyKdbFormat", result.EntriesAdded, result.GroupsAdded), ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task ExportVaultStylesheetAsync()
    {
        if (_database is not { } vault || PickOpenFileAsync is null || PickSaveFileAsync is null) return;
        var stylesheet = await PickOpenFileAsync("xsl");
        if (string.IsNullOrWhiteSpace(stylesheet) || _database != vault) return;
        var output = await PickSaveFileAsync($"{vault.Meta.DatabaseName}.html", "*");
        if (string.IsNullOrWhiteSpace(output) || _database != vault) return;
        try
        {
            await using var input = File.OpenRead(stylesheet);
            var bytes = VaultStylesheetExport.Transform(vault, input);
            await File.WriteAllBytesAsync(output, bytes);
            ShowToast(L("Str_StylesheetExported"), ToastSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or System.Xml.Xsl.XsltException)
        {
            ShowToast(L("Str_StylesheetExportFailed"), ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportEmergencySheetAsync()
    {
        if (_database is null || PickSaveFileAsync is null) return;
        var path = await PickSaveFileAsync(L("Str_EmergencySheetHtmlFormat", _database.Meta.DatabaseName), "html");
        if (string.IsNullOrWhiteSpace(path)) return;
        await File.WriteAllTextAsync(path, ImportExportService.ExportEmergencySheetHtml(_database));
        ShowToast(L("Str_GeneratedEmergencySheetFormat", Path.GetFileName(path)));
    }

    private async Task CopySecretAsync(string value, string label)
    {
        if (CopyToClipboardAsync is null || string.IsNullOrEmpty(value)) return;
        await CopyToClipboardAsync(value);
        _lastCopiedSecret = value;

        int seconds = Math.Max(5, SettingsService.Settings.ClipboardClearSeconds);
        ShowToast(L("Str_CopiedSecureClipboardClearsInSecondsFormat", label, seconds), ToastSeverity.Success);

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
                            ShowToast(L("Str_SecureClipboardCleared"), ToastSeverity.Success);
                        }
                    }
                });
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    public void ShowToast(string message, ToastSeverity severity = ToastSeverity.Information)
    {
        _toastTimer.Stop();
        ToastKind = severity;
        ToastMessage = message;
        IsToastVisible = true;
        _toastTimer.Start();
    }

    private static void UpdateObservable(ObservableCollection<EntryItemViewModel> target, List<KdbxEntry> items)
    {
        target.Clear();
        foreach (var e in items)
            target.Add(new EntryItemViewModel(e));
    }
}
