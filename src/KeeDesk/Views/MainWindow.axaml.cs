using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KeeDesk.Helpers;
using KeeDesk.Services;
using KeeDesk.ViewModels;

namespace KeeDesk.Views;

public partial class MainWindow : Window
{
    private const string EntryDragFormatId = "com.keedesk.entry-uuid";
    private static readonly DataFormat<string> EntryDragFormat = DataFormat.CreateStringApplicationFormat(EntryDragFormatId);
    private Avalonia.Point? _entryDragStartPoint;
    private PointerPressedEventArgs? _entryDragPressedEvent;
    private EntryItemViewModel? _entryDragCandidate;
    private KeeDesk.Services.GlobalHotKeyService? _globalHotKeyService;
    private KeeDesk.Services.SystemSleepLockService? _sleepLockService;

    private const double ExpandedWidth = 1180;
    private const double StackedWidth = 900;
    private bool? _sidebarExpanded;
    private bool _stacked;
    private bool _detailOpen;
    private bool _searchPendingSelection;
    private MainWindowViewModel? _layoutViewModel;
    private EntryItemViewModel? _revealedEntry;
    private int _workspaceDialogCount;
    private readonly AppSettingsService? _settingsService;
    private Size? _normalClientSize;
    private bool _sizeCapturePending;
    private readonly ScaleTransform _totpProgressScale = new(0, 1);
    private bool _totpFramePending;

    public MainWindow() : this(null) { }

    public MainWindow(AppSettingsService? settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();
        TotpProgressFill.RenderTransform = _totpProgressScale;
        TotpProgressTrack.LayoutUpdated += (_, _) => UpdateTotpProgress();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateTotpProgress();
        };
        if (MacVaultPicker.Flyout is MenuFlyout vaultSwitcher)
            vaultSwitcher.Opening += VaultSwitcher_Opening;
        if (DesktopWindowChrome.ButtonsOnLeft) MinWidth = 960;
        if (OperatingSystem.IsWindows())
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
            PropertyChanged += (_, e) =>
            {
                if (e.Property == ActualTransparencyLevelProperty)
                    Classes.Set("mica", ActualTransparencyLevel == WindowTransparencyLevel.Mica);
            };
        }
        PasswordRevealButton.AddHandler(PointerPressedEvent, RevealPassword_PointerPressed, RoutingStrategies.Tunnel);
        PasswordRevealButton.AddHandler(PointerReleasedEvent, (_, _) => HidePassword(), RoutingStrategies.Tunnel, handledEventsToo: true);
        PasswordRevealButton.PointerCaptureLost += (_, _) => HidePassword();
        PasswordRevealButton.PointerExited += (_, _) => HidePassword();
        PasswordRevealButton.AddHandler(KeyDownEvent, RevealPassword_KeyDown, RoutingStrategies.Tunnel);
        PasswordRevealButton.AddHandler(KeyUpEvent, (_, _) => HidePassword(), RoutingStrategies.Tunnel, handledEventsToo: true);
        PasswordRevealButton.LostFocus += (_, _) => HidePassword();
        Deactivated += (_, _) =>
        {
            HidePassword();
            CloseSearchSuggestions();
        };

        Resized += (_, _) =>
        {
            if (_enteringWorkspace && !WorkspaceStage.IsVisible) return;
            UpdateWorkspaceLayout();
            if (!_workspaceReady || _settingsService is null || _sizeCapturePending) return;
            _sizeCapturePending = true;
            // Some platforms report the resize before the corresponding window-state change.
            Dispatcher.UIThread.Post(() =>
            {
                _sizeCapturePending = false;
                CaptureNormalWindowSize();
            });
        };
        Closed += (_, _) =>
        {
            ++_presentationVersion;
            SaveWindowSize();
            if (_layoutViewModel is not null)
                _layoutViewModel.PropertyChanged -= OnWorkspaceStateChanged;
            _layoutViewModel = null;
        };
        DataContextChanged += (_, _) => ObserveWorkspace();
        Opened += (_, _) =>
        {
            WireViewModelCallbacks();
            UpdateWorkspaceLayout();
            CaptureNormalWindowSize();
            UpdateTotpProgress();
        };
        KeyDown += OnWindowKeyDown;

        // 监听用户交互（鼠标按下、鼠标移动、滚轮、键盘按键）以刷新空闲自动锁定计时器
        AddHandler(PointerPressedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) => { if (_enteringWorkspace) e.Handled = true; }, RoutingStrategies.Tunnel);

        // 注册全局文件拖拽打开（支持拖入 .kdbx 密码库或 .keyx 密钥文件）
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);

        // macOS 下将交通灯按钮按 48pt 标题栏对称微调至右下居中位置
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 48, right =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.UpdateMacTitleBarInsets(right);
        });
    }

    private void UpdateTotpProgress()
    {
        if (_totpFramePending || !IsVisible || !TotpProgressTrack.IsEffectivelyVisible || WindowState == WindowState.Minimized
            || _layoutViewModel is not { IsUnlocked: true, SelectedEntry: { HasTotp: true } entry })
            return;

        _totpProgressScale.ScaleX = entry.GetTotpProgress(DateTimeOffset.UtcNow);
        _totpFramePending = true;
        RequestAnimationFrame(OnTotpAnimationFrame);
    }

    private void OnTotpAnimationFrame(TimeSpan _)
    {
        _totpFramePending = false;
        UpdateTotpProgress();
    }

    private Size GetWorkspaceSize()
    {
        var size = _normalClientSize ?? WelcomeSize;
        if (_normalClientSize is null && _settingsService?.Settings is { MainWindowWidth: double width, MainWindowHeight: double height }
            && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
            size = new Size(width, height);

        var maxWidth = double.PositiveInfinity;
        var maxHeight = double.PositiveInfinity;
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { Scaling: > 0 } screen
            && screen.WorkingArea.Width > 0 && screen.WorkingArea.Height > 0)
        {
            var chrome = (FrameSize ?? ClientSize) - ClientSize;
            maxWidth = Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling - Math.Max(0, chrome.Width));
            maxHeight = Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - Math.Max(0, chrome.Height));
        }
        return new Size(Math.Clamp(size.Width, MinWidth, maxWidth), Math.Clamp(size.Height, MinHeight, maxHeight));
    }

    private void CaptureNormalWindowSize()
    {
        var size = ClientSize;
        if (_workspaceReady && WindowState == WindowState.Normal && double.IsFinite(size.Width) && double.IsFinite(size.Height)
            && size.Width >= MinWidth && size.Height >= MinHeight)
            _normalClientSize = size;
    }

    private void SaveWindowSize()
    {
        if (_settingsService is not { Settings: var settings }) return;
        CaptureNormalWindowSize();
        if (_normalClientSize is not { } size || (settings.MainWindowWidth == size.Width && settings.MainWindowHeight == size.Height)) return;
        settings.MainWindowWidth = size.Width;
        settings.MainWindowHeight = size.Height;
        _settingsService.Save();
    }

    private void ObserveWorkspace()
    {
        HidePassword();
        if (_layoutViewModel is not null)
            _layoutViewModel.PropertyChanged -= OnWorkspaceStateChanged;
        _layoutViewModel = DataContext as MainWindowViewModel;
        if (_layoutViewModel is not null)
            _layoutViewModel.PropertyChanged += OnWorkspaceStateChanged;
        if (_layoutViewModel?.IsUnlocked == true)
            PresentWorkspaceImmediately();
        else
            PresentWelcome();
        UnlockKeyOptions.IsExpanded = _layoutViewModel?.HasUnlockKeyFile == true;
        _detailOpen = false;
        _searchPendingSelection = false;
        UpdateWorkspaceLayout();
    }

    private void OnWorkspaceStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HasUnlockKeyFile) && _layoutViewModel?.HasUnlockKeyFile == true)
            UnlockKeyOptions.IsExpanded = true;
        if (e.PropertyName == nameof(MainWindowViewModel.IsUnlocked))
        {
            MacVaultPicker.Flyout?.Hide();
            WelcomeSettingsButton.Flyout?.Hide();
            if (_layoutViewModel?.IsUnlocked == true)
                BeginWorkspaceTransition();
            else
                PresentWelcome();
        }
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedEntry) or nameof(MainWindowViewModel.IsUnlocked))
            HidePassword();
        if (e.PropertyName is nameof(MainWindowViewModel.IsUnlocked) or nameof(MainWindowViewModel.ActiveSection)
            or nameof(MainWindowViewModel.SearchQuery))
        {
            _detailOpen = false;
            _searchPendingSelection = _layoutViewModel is { IsUnlocked: true, HasSearchQuery: true };
            UpdateWorkspaceLayout();
            if (_layoutViewModel?.IsUnlocked != true) CloseSearchSuggestions();
        }
    }

    private void UpdateWorkspaceLayout()
    {
        double width = ClientSize.Width > 0 ? ClientSize.Width : Width;
        bool desktop = !DesktopWindowChrome.ButtonsOnLeft;
        bool compact = desktop && width < ExpandedWidth;
        bool rail = desktop && !(_sidebarExpanded ?? !compact);
        _stacked = desktop && width < StackedWidth + (!rail && compact ? 176 : 0);
        if (!_stacked) _detailOpen = false;

        Classes.Set("compactChrome", compact);
        Classes.Set("rail", rail);
        WorkspaceSidebar.Width = rail ? 64 : 240;
        UnlockSidebar.Width = width < StackedWidth ? 224 : width < ExpandedWidth ? 248 : 272;
        DesktopSearchSlot.Width = width < StackedWidth ? 244 : compact ? 310 : width < 1280 ? 320 : 360;
        DesktopCommands.Margin = new Avalonia.Thickness(0, 0, width < StackedWidth ? 164 : 192, 0);
        WorkspaceBackButton.IsVisible = _stacked && _detailOpen && _layoutViewModel?.IsUnlocked == true;

        DetailSelectionRegion.IsVisible = !_searchPendingSelection;
        SearchDetailsPlaceholder.IsVisible = _searchPendingSelection;
        bool noMatches = _layoutViewModel?.FilteredEntries.Count == 0;
        SearchResultMessage.Text = noMatches ? "未找到匹配的条目" : "选择一条搜索结果";
        ClearEmptySearchButton.IsVisible = noMatches;
        EntryPane.IsVisible = !_stacked || !_detailOpen;
        EntryDetail.IsVisible = !_stacked || _detailOpen;
        EntryDivider.IsVisible = !_stacked;
        EntryWorkspace.ColumnDefinitions[0].Width = _stacked
            ? (_detailOpen ? new GridLength(0) : GridLength.Star)
            : new GridLength(width < ExpandedWidth ? 280 : 344);
        EntryWorkspace.ColumnDefinitions[1].Width = new GridLength(_stacked ? 0 : 1);
        EntryWorkspace.ColumnDefinitions[2].Width = _stacked && !_detailOpen ? new GridLength(0) : GridLength.Star;
    }

    private void ToggleSidebar_Click(object? sender, RoutedEventArgs e)
    {
        _sidebarExpanded = Classes.Contains("rail");
        UpdateWorkspaceLayout();
    }

    private void VaultSwitcher_Opening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || _layoutViewModel is not { } vm) return;
        while (menu.Items.Count > 0 && menu.Items[0] is MenuItem { Tag: VaultTabSession })
            menu.Items.RemoveAt(0);

        var labelTemplate = (IDataTemplate)this.FindResource("VaultSwitcherLabel")!;
        var iconTemplate = (IDataTemplate)this.FindResource("VaultSwitcherIcon")!;
        for (var i = 0; i < vm.OpenVaults.Count; i++)
        {
            var session = vm.OpenVaults[i];
            var item = new MenuItem
            {
                Tag = session,
                Header = session,
                HeaderTemplate = labelTemplate,
                Icon = new ContentControl
                {
                    Content = session,
                    ContentTemplate = iconTemplate,
                },
            };
            item.Classes.Add("vaultChoice");
            item.Classes.Set("currentVault", session.IsActive);
            ToolTip.SetTip(item, session.DisplayName);
            Avalonia.Automation.AutomationProperties.SetName(item, session.DisplayName);
            item.Click += SwitchVaultFromMenu_Click;
            menu.Items.Insert(i, item);
        }
    }

    private void SwitchVaultFromMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: VaultTabSession { IsActive: false } session } || _layoutViewModel is not { } vm) return;
        _detailOpen = false;
        vm.SwitchVaultTabCommand.Execute(session);
        UpdateWorkspaceLayout();
    }

    private void EntryList_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null)
            return;
        AcceptEntrySelection();
    }

    private void AcceptEntrySelection()
    {
        _searchPendingSelection = false;
        CloseSearchSuggestions();
        UpdateWorkspaceLayout();
        OpenStackedDetails();
    }

    private void EntryList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _layoutViewModel?.SelectedEntry is null) return;
        AcceptEntrySelection();
        e.Handled = true;
    }

    private bool OpenStackedDetails()
    {
        if (!_stacked || _layoutViewModel is not { SelectedEntry: not null, HasMultipleSelectedEntries: false })
            return false;
        _detailOpen = true;
        UpdateWorkspaceLayout();
        WorkspaceBackButton.Focus();
        return true;
    }

    private void WorkspaceBack_Click(object? sender, RoutedEventArgs e) => BackToEntries();

    private void AuditEntry_Click(object? sender, RoutedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(AcceptEntrySelection);

    private void BackToEntries()
    {
        _detailOpen = false;
        UpdateWorkspaceLayout();
        EntryListBox.Focus();
    }

    private void RevealPassword_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(PasswordRevealButton).Properties.IsLeftButtonPressed)
            RevealPassword();
    }

    private void RevealPassword_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        RevealPassword();
        e.Handled = true;
    }

    private void RevealPassword()
    {
        if (_layoutViewModel is not { IsUnlocked: true, SelectedEntry: { } entry }) return;
        _revealedEntry = entry;
        entry.IsPasswordRevealed = true;
    }

    private void HidePassword()
    {
        if (_revealedEntry is { } entry)
        {
            entry.IsPasswordRevealed = false;
            _revealedEntry = null;
        }
    }

    private (TextBox Field, Popup Popup, ListBox List) SearchControls => DesktopWindowChrome.ButtonsOnLeft
        ? (MacGlobalSearchBox, MacSearchPopup, MacSearchSuggestions)
        : (WinGlobalSearchBox, WinSearchPopup, WinSearchSuggestions);

    private void Search_FocusChanged(object? sender, RoutedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateSearchSuggestions);

    private void Search_TextChanged(object? sender, TextChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateSearchSuggestions);

    private void UpdateSearchSuggestions()
    {
        var (field, popup, list) = SearchControls;
        if (popup.IsOpen && list.IsKeyboardFocusWithin) return;
        if (!field.IsEffectivelyVisible || !field.IsKeyboardFocusWithin
            || _layoutViewModel is not { IsUnlocked: true, HasSearchQuery: true } vm
            || vm.FilteredEntries.Count == 0)
        {
            popup.IsOpen = false;
            return;
        }
        list.ItemsSource = vm.FilteredEntries.Take(5).ToArray();
        list.SelectedIndex = 0;
        popup.IsOpen = true;
    }

    private void CloseSearchSuggestions()
    {
        WinSearchPopup.IsOpen = false;
        MacSearchPopup.IsOpen = false;
    }

    private void SearchSuggestion_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            SubmitSearch();
    }

    private void SubmitSearch()
    {
        if (_layoutViewModel is not { } vm || vm.FilteredEntries.Count == 0) return;
        var (_, popup, list) = SearchControls;
        if (popup.IsOpen && list.SelectedItem is EntryItemViewModel suggestion)
            vm.SelectedEntry = suggestion;
        AcceptEntrySelection();
        if (!_stacked) EntryListBox.Focus();
    }

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_layoutViewModel is not { } vm) return;
        if (e.Key == Key.Escape && vm.HasSearchQuery)
        {
            ClearSearch_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && vm.FilteredEntries.Count > 0)
        {
            SubmitSearch();
            e.Handled = true;
        }
        else if ((e.Key is Key.Down or Key.Up) && sender is TextBox && SearchControls is (_, { IsOpen: true }, var list))
        {
            int step = e.Key == Key.Down ? 1 : -1;
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + step, 0, list.ItemCount - 1);
            list.ScrollIntoView(list.SelectedIndex);
            e.Handled = true;
        }
    }

    private async Task<T> ShowWorkspaceDialogAsync<T>(Window dialog)
    {
        DialogSmoke.IsVisible = ++_workspaceDialogCount > 0;
        try
        {
            return await dialog.ShowDialog<T>(this);
        }
        finally
        {
            DialogSmoke.IsVisible = --_workspaceDialogCount > 0;
        }
    }

    private async void MoveEntryToRecycleBin_Click(object? sender, RoutedEventArgs e)
    {
        if (_layoutViewModel is not { IsUnlocked: true } vm) return;
        var entry = (sender as MenuItem)?.CommandParameter as EntryItemViewModel ?? vm.SelectedEntry;
        if (entry is null) return;
        var database = vm.CurrentDatabase;
        if (await ShowWorkspaceDialogAsync<bool>(new RecycleEntryDialog(entry.Title))
            && vm.IsUnlocked && ReferenceEquals(vm.CurrentDatabase, database))
        {
            vm.DeleteEntryCommand.Execute(entry);
            if (_stacked) BackToEntries();
        }
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (_layoutViewModel is not { } vm) return;
        vm.SearchQuery = string.Empty;
        CloseSearchSuggestions();
        SearchControls.Field.Focus();
    }

    private void OnUserActivity(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RecordUserActivity();
    }

    private void WireViewModelCallbacks()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        vm.CopyToClipboardAsync = async text =>
        {
            if (string.IsNullOrEmpty(text))
            {
                if (Clipboard is not null) await Clipboard.ClearAsync();
                return;
            }

            // 优先使用防剪贴板历史留痕的原生写入（macOS ConcealedType/TransientType · Windows ExcludeFromHistory）
            if (KeeDesk.Services.NativeClipboardSecurityService.TrySetConcealedText(text))
                return;

            if (Clipboard is null) return;
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await Clipboard.SetDataAsync(data);
        };

        vm.GetClipboardTextAsync = async () =>
        {
            if (Clipboard is null) return null;
            return await Clipboard.TryGetTextAsync();
        };

        vm.OpenEntryEditorAsync = async (entry, group, showHistory) =>
        {
            var dlg = new EntryEditorWindow(entry, group, vm.CurrentDatabase, showHistory);
            var saved = await ShowWorkspaceDialogAsync<bool>(dlg);
            if (saved && !string.IsNullOrEmpty(dlg.SavedPasswordForClipboard) && vm.CopyToClipboardAsync is not null)
            {
                await vm.CopyToClipboardAsync(dlg.SavedPasswordForClipboard);
                vm.ShowToast("已保存并自动复制密码到安全剪贴板", ToastSeverity.Success);
            }
            return saved;
        };

        vm.OpenPasswordGeneratorAsync = async () =>
        {
            var dlg = new PasswordGeneratorWindow();
            return await ShowWorkspaceDialogAsync<string?>(dlg);
        };

        vm.OpenSettingsAsync = async () =>
        {
            var dlg = new SettingsWindow(vm);
            await dlg.ShowDialog(this);
        };

        vm.OpenAboutAsync = async () =>
        {
            var dlg = new SettingsWindow(vm, initialTabIndex: 4);
            await dlg.ShowDialog(this);
        };

        vm.ShowUpdateWindowAsync = async release =>
        {
            var dlg = new UpdateWindow(release);
            await dlg.ShowDialog(this);
        };

        // 启动后后台静默检查 GitHub Releases 更新
        _ = vm.CheckForUpdatesCoreAsync(silent: true);

        vm.FocusSearchRequested += () =>
        {
            if (!vm.IsUnlocked) return;
            var box = SearchControls.Field;
            box.Focus();
            box.SelectAll();
        };

        // 密码错误时触发 macOS 原生风格的丝滑水平微颤动画
        vm.UnlockFailedShakeRequested += () => _ = PlayPasswordShakeAsync();

        vm.PickOpenFileAsync = async ext =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = ext == "*"
                    ? [FilePickerFileTypes.All]
                    : [new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = [$"*.{ext}"] }, FilePickerFileTypes.All]
            });
            return files.FirstOrDefault()?.TryGetLocalPath();
        };

        vm.PickSaveFileAsync = async (suggestedName, ext) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = suggestedName,
                DefaultExtension = ext == "*" ? null : ext,
                FileTypeChoices = ext == "*"
                    ? [FilePickerFileTypes.All]
                    : [new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = [$"*.{ext}"] }]
            });
            return file?.TryGetLocalPath();
        };

        vm.OpenGroupDialogAsync = async (heading, initialName, initialNotes) =>
        {
            var dlg = new GroupDialogWindow(heading, initialName, initialNotes);
            var res = await dlg.ShowDialog<GroupDialogResult?>(this);
            return res is null ? null : (res.Name, res.Notes);
        };

        vm.OpenDatabaseSettingsAsync = async db =>
        {
            var dlg = new DatabaseSettingsWindow(db);
            var res = await dlg.ShowDialog<DatabaseSettingsResult?>(this);
            return res is null ? (false, null, null) : (res.Saved, res.NewMasterKey, res.NewKeyFilePath);
        };

        vm.OpenVaultUnlockPromptAsync = async (vaultPath, heading, confirmText) =>
        {
            var dlg = new VaultUnlockPromptWindow(vaultPath, heading, confirmText);
            var res = await dlg.ShowDialog<VaultUnlockResult?>(this);
            return res is null ? null : (res.Database, res.MasterKey, res.KeyFilePath);
        };

        vm.OpenMoveToGroupAsync = async (db, entry) =>
        {
            var dlg = new MoveToGroupWindow(db, entry);
            return await dlg.ShowDialog<KeeDesk.Core.Kdbx.KdbxGroup?>(this);
        };

        vm.OpenDatabaseMaintenanceAsync = async db =>
        {
            var dlg = new DatabaseMaintenanceWindow(db);
            return await dlg.ShowDialog<bool>(this);
        };

        vm.OpenAdvancedSearchAsync = async () =>
        {
            var dlg = new AdvancedSearchWindow();
            return await dlg.ShowDialog<KeeDesk.Core.Services.EntrySearchParameters?>(this);
        };

        vm.OpenUrlExternal = url =>
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        };

        vm.MinimizeWindowAction = () => WindowState = WindowState.Minimized;

        vm.OpenAutoTypePickerAsync = async (targetTitle, matches) =>
        {
            var dlg = new AutoTypePickerWindow(targetTitle, matches);
            return await dlg.ShowDialog<KeeDesk.Core.Services.AutoTypeMatchResult?>(this);
        };

        // 注册 OS 级全局 Auto-Type 热键（Windows: Ctrl+Alt+A / macOS: Control+Option+A）
        _globalHotKeyService?.Dispose();
        _globalHotKeyService = new KeeDesk.Services.GlobalHotKeyService();
        _globalHotKeyService.HotKeyPressed += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (vm.IsUnlocked)
                    vm.TriggerGlobalAutoTypeCommand.Execute(null);
            });
        };
        _globalHotKeyService.TryRegisterDefault();

        // 注册系统休眠与锁屏自动锁定监听
        _sleepLockService?.Dispose();
        _sleepLockService = new KeeDesk.Services.SystemSleepLockService();
        _sleepLockService.LockRequested += reason =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!vm.IsUnlocked) return;
                vm.LockVaultCommand.Execute(null);
                vm.ShowToast(reason);
                KeeDesk.Services.NativeNotificationService.Send("KeeDesk", reason);
            });
        };

        Closed += (_, _) =>
        {
            _globalHotKeyService?.Dispose();
            _sleepLockService?.Dispose();
        };
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>();
        vm.HandleDroppedFiles(paths);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsUnlocked || !_workspaceReady) return;
        bool mod = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_stacked && _detailOpen && e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt)
        {
            BackToEntries();
            e.Handled = true;
            return;
        }
        if (!mod) return;

        if (e.Key == Key.K)
        {
            var box = SearchControls.Field;
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.S)
        {
            vm.SaveVaultCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            vm.LockVaultCommand.Execute(null);
            e.Handled = true;
        }
        else if (!vm.IsMacOS && e.Key == Key.G)
        {
            vm.OpenPasswordGeneratorCommandCommand.Execute(null);
            e.Handled = true;
        }
        else if (!vm.IsMacOS && e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
        {
            vm.OpenAnotherVaultCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.N || e.Key == Key.I)
        {
            vm.AddEntryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.B)
        {
            vm.CopyUserNameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && shift)
        {
            vm.CopyPasswordCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.T && shift)
        {
            vm.CopyTotpCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.D)
        {
            vm.DuplicateEntryCommand.Execute(vm.SelectedEntry);
            e.Handled = true;
        }
        else if (e.Key == Key.U)
        {
            vm.OpenEntryUrlCommand.Execute(vm.SelectedEntry);
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            vm.OpenAdvancedSearchAsyncCommandCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async Task PlayPasswordShakeAsync()
    {
        var transform = new Avalonia.Media.TranslateTransform();
        UnlockPasswordContainer.RenderTransform = transform;
        double[] offsets = [-10, 10, -7, 7, -4, 4, 0];
        foreach (var dx in offsets)
        {
            transform.X = dx;
            await Task.Delay(38);
        }
        if (_layoutViewModel?.IsUnlocked == true) return;
        UnlockPasswordBox.Focus();
        UnlockPasswordBox.SelectAll();
    }

    private void UnlockPassword_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
            vm.UnlockVaultCommand.Execute(null);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_enteringWorkspace || e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // 避免点击标题栏内的输入框或按钮时误触发窗口拖动
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or TextBox or CheckBox or ComboBox)
                return;
            src = src.GetVisualParent();
        }

        BeginMoveDrag(e);
    }

    private void OpenUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.OpenEntryUrlCommand.Execute(vm.SelectedEntry);
    }

    private void EntryListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is ListBox lb && lb.SelectedItems is not null)
            vm.UpdateSelectedEntries(lb.SelectedItems.OfType<EntryItemViewModel>());
    }

    // ==================== 条目拖拽到左侧分组树 ====================
    private void EntryCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: EntryItemViewModel item })
        {
            _entryDragStartPoint = e.GetPosition(this);
            _entryDragPressedEvent = e;
            _entryDragCandidate = item;
        }
    }

    private async void EntryCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_entryDragStartPoint is null || _entryDragCandidate is null || _entryDragPressedEvent is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _entryDragStartPoint = null;
            _entryDragPressedEvent = null;
            _entryDragCandidate = null;
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _entryDragStartPoint.Value.X) < 8 &&
            Math.Abs(pos.Y - _entryDragStartPoint.Value.Y) < 8)
            return;

        var entry = _entryDragCandidate;
        var pressedEvent = _entryDragPressedEvent;
        _entryDragStartPoint = null;
        _entryDragPressedEvent = null;
        _entryDragCandidate = null;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(EntryDragFormat, entry.Model.Uuid.ToString("D")));
        await DragDrop.DoDragDropAsync(pressedEvent, data, DragDropEffects.Move);
    }

    private void GroupItem_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(EntryDragFormat))
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void GroupItem_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Control { DataContext: GroupItemViewModel targetGroup }) return;
        if (!e.DataTransfer.Contains(EntryDragFormat)) return;

        var raw = e.DataTransfer.TryGetValue(EntryDragFormat);
        if (Guid.TryParse(raw, out var entryUuid))
        {
            vm.MoveEntryByUuidToGroup(entryUuid, targetGroup);
            e.Handled = true;
        }
    }
}
