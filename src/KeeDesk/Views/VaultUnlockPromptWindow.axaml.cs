using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using KeeDesk.Core.Kdbx;
using KeeDesk.Helpers;

namespace KeeDesk.Views;

/// <summary>
/// 弹窗解锁结果（用于多标签打开新密码库或合并同步外部密码库）。
/// </summary>
public sealed record VaultUnlockResult(KdbxDatabase Database, CompositeKey MasterKey, string KeyFilePath);

/// <summary>
/// 解锁外部 .kdbx 密码库时用于输入目标库主密钥并异步解密的通用对话框。
/// </summary>
public partial class VaultUnlockPromptWindow : Window
{
    private readonly string _vaultPath;
    private string _keyFilePath = string.Empty;
    private bool _isWorking;

    public VaultUnlockPromptWindow() : this(string.Empty, "解锁密码库", "解锁") { }

    public VaultUnlockPromptWindow(string vaultPath, string heading = "合并 / 同步其他密码库", string confirmButtonText = "解密并合并")
    {
        InitializeComponent();
        _vaultPath = vaultPath;
        Title = heading;
        DialogHeading.Text = heading;
        MergeBtn.Content = confirmButtonText;
        FilePathLabel.Text = Path.GetFileName(vaultPath);
        UseWindowsUserAccountCheck.IsVisible = OperatingSystem.IsWindows();
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 40);
        Opened += (_, _) => PasswordBox.Focus();
    }

    private async void BrowseKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            _keyFilePath = path;
            KeyFileBox.Text = Path.GetFileName(path);
        }
    }

    private async void Merge_Click(object? sender, RoutedEventArgs e)
    {
        if (_isWorking) return;
        _isWorking = true;
        ErrorText.IsVisible = false;
        UnlockProgress.IsVisible = true;
        MergeBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;

        var pwd = PasswordBox.Text ?? string.Empty;
        var keyFile = _keyFilePath;
        var useWua = OperatingSystem.IsWindows() && UseWindowsUserAccountCheck.IsChecked == true;
        try
        {
            var (db, key) = await Task.Run(() =>
            {
                var k = CompositeKey.FromPasswordAndKeyFile(pwd, keyFile, useWua);
                var loaded = KdbxFormat.Load(_vaultPath, k);
                return (loaded, k);
            });
            Close(new VaultUnlockResult(db, key, keyFile));
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"解密失败：主密码或密钥文件不正确（{ex.Message}）";
            ErrorText.IsVisible = true;
            PasswordBox.Focus();
            PasswordBox.SelectAll();
        }
        finally
        {
            _isWorking = false;
            UnlockProgress.IsVisible = false;
            MergeBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void PasswordBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Merge_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or TextBox) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
