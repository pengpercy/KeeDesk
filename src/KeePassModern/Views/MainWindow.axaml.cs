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
                DefaultExtension = ext,
                FileTypeChoices = [new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = [$"*.{ext}"] }]
            });
            return file?.TryGetLocalPath();
        };
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsUnlocked) return;
        bool mod = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control);
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
        else if (e.Key == Key.N)
        {
            vm.AddEntryCommand.Execute(null);
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
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrWhiteSpace(vm.SelectedEntry?.Url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(vm.SelectedEntry.Url) { UseShellExecute = true });
        }
        catch { }
    }
}
