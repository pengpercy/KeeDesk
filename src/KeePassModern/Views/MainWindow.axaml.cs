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
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => WireViewModelCallbacks();
        KeyDown += OnWindowKeyDown;

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

    private void WireViewModelCallbacks()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        vm.CopyToClipboardAsync = async text =>
        {
            if (Clipboard is null) return;
            if (string.IsNullOrEmpty(text))
            {
                await Clipboard.ClearAsync();
                return;
            }
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

        vm.OpenVaultUnlockPromptAsync = async vaultPath =>
        {
            var dlg = new VaultUnlockPromptWindow(vaultPath);
            return await dlg.ShowDialog<KeePassModern.Core.Kdbx.KdbxDatabase?>(this);
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
}
