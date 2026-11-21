using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using KeePassModern.Helpers;
using KeePassModern.ViewModels;

namespace KeePassModern.Views;

public partial class MainWindow : Window
{
    private const string EntryDragFormatId = "com.keepassmodern.entry-uuid";
    private static readonly DataFormat<string> EntryDragFormat = DataFormat.CreateStringApplicationFormat(EntryDragFormatId);
    private Avalonia.Point? _entryDragStartPoint;
    private PointerPressedEventArgs? _entryDragPressedEvent;
    private EntryItemViewModel? _entryDragCandidate;
    private KeePassModern.Services.GlobalHotKeyService? _globalHotKeyService;
    private KeePassModern.Services.SystemSleepLockService? _sleepLockService;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => WireViewModelCallbacks();
        KeyDown += OnWindowKeyDown;

        // 监听用户交互（鼠标按下、鼠标移动、滚轮、键盘按键）以刷新空闲自动锁定计时器
        AddHandler(PointerPressedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnUserActivity, RoutingStrategies.Tunnel);

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
            if (KeePassModern.Services.NativeClipboardSecurityService.TrySetConcealedText(text))
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

        vm.OpenEntryEditorAsync = async (entry, group) =>
        {
            var dlg = new EntryEditorWindow(entry, group, vm.CurrentDatabase);
            var saved = await dlg.ShowDialog<bool>(this);
            if (saved && !string.IsNullOrEmpty(dlg.SavedPasswordForClipboard) && vm.CopyToClipboardAsync is not null)
            {
                await vm.CopyToClipboardAsync(dlg.SavedPasswordForClipboard);
                vm.ShowToast("已保存并自动复制密码到安全剪贴板");
            }
            return saved;
        };

        vm.OpenPasswordGeneratorAsync = async () =>
        {
            var dlg = new PasswordGeneratorWindow();
            return await dlg.ShowDialog<string?>(this);
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
            var box = vm.IsMacOS ? MacGlobalSearchBox : WinGlobalSearchBox;
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
            return await dlg.ShowDialog<KeePassModern.Core.Kdbx.KdbxGroup?>(this);
        };

        vm.OpenDatabaseMaintenanceAsync = async db =>
        {
            var dlg = new DatabaseMaintenanceWindow(db);
            return await dlg.ShowDialog<bool>(this);
        };

        vm.OpenAdvancedSearchAsync = async () =>
        {
            var dlg = new AdvancedSearchWindow();
            return await dlg.ShowDialog<KeePassModern.Core.Services.EntrySearchParameters?>(this);
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
            return await dlg.ShowDialog<KeePassModern.Core.Services.AutoTypeMatchResult?>(this);
        };

        // 注册 OS 级全局 Auto-Type 热键（Windows: Ctrl+Alt+A / macOS: Control+Option+A）
        _globalHotKeyService?.Dispose();
        _globalHotKeyService = new KeePassModern.Services.GlobalHotKeyService();
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
        _sleepLockService = new KeePassModern.Services.SystemSleepLockService();
        _sleepLockService.LockRequested += reason =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!vm.IsUnlocked) return;
                vm.LockVaultCommand.Execute(null);
                vm.ShowToast(reason);
                KeePassModern.Services.NativeNotificationService.Send("KeePassModern", reason);
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
        if (DataContext is not MainWindowViewModel vm || !vm.IsUnlocked) return;
        bool mod = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (!mod) return;

        if (e.Key == Key.K)
        {
            var box = vm.IsMacOS ? MacGlobalSearchBox : WinGlobalSearchBox;
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
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
