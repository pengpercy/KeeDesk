using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using KeeDesk.Helpers;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

/// <summary>
/// 密码库设置弹窗返回结果：若用户更改了主密钥，则携带新的 CompositeKey 与密钥文件路径。
/// </summary>
public sealed record DatabaseSettingsResult(bool Saved, CompositeKey? NewMasterKey, string? NewKeyFilePath);

public partial class DatabaseSettingsWindow : Window
{
    private readonly KdbxDatabase _database;
    private string _newKeyFilePath = string.Empty;

    public DatabaseSettingsWindow() : this(KdbxDatabase.CreateDefault()) { }

    public DatabaseSettingsWindow(KdbxDatabase database)
    {
        InitializeComponent();
        _database = database;
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 44);
        LoadFromDatabase();
    }

    private void LoadFromDatabase()
    {
        VaultFileSubTitle.Text = string.IsNullOrWhiteSpace(_database.FilePath)
            ? L("Str_ThisDemoIsInMemorySaveIt")
            : _database.FilePath;

        DbNameBox.Text = _database.Meta.DatabaseName;
        DbDescBox.Text = _database.Meta.DatabaseDescription;
        DefaultUserBox.Text = _database.Meta.DefaultUserName;

        CipherCombo.SelectedIndex = _database.Cipher switch
        {
            KdbxCipherAlgorithm.ChaCha20 => 1,
            KdbxCipherAlgorithm.Twofish => 2,
            _ => 0
        };

        KdfCombo.SelectedIndex = _database.Kdf switch
        {
            KdbxKdfAlgorithm.Argon2d => 1,
            KdbxKdfAlgorithm.AesKdf => 2,
            _ => 0
        };

        CompressionCombo.SelectedIndex = _database.Compression == KdbxCompression.None ? 1 : 0;

        ArgonIterationsNum.Value = (decimal)Math.Clamp(_database.KdfIterations, 1UL, 100UL);
        ArgonMemoryNum.Value = (decimal)Math.Clamp(_database.KdfMemoryBytes / (1024UL * 1024UL), 8UL, 1024UL);
        ArgonParallelismNum.Value = Math.Clamp(_database.KdfParallelism, 1U, 16U);
        AesRoundsNum.Value = (decimal)Math.Clamp(_database.AesKdfRounds, 10_000UL, 10_000_000UL);

        RecycleBinToggle.IsChecked = _database.Meta.RecycleBinEnabled;
        HistoryMaxItemsNum.Value = Math.Clamp(_database.Meta.HistoryMaxItems, 0, 100);
        UseWindowsUserAccountCheck.IsVisible = OperatingSystem.IsWindows();
        UpdateKdfPanels();
    }

    private void KdfCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateKdfPanels();

    private void UpdateKdfPanels()
    {
        if (Argon2Panel is null || AesKdfPanel is null) return;
        bool isAes = KdfCombo.SelectedIndex == 2;
        Argon2Panel.IsVisible = !isAes;
        AesKdfPanel.IsVisible = isAes;
    }

    private void ChangeMasterKeyToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (MasterKeyPanel is not null)
            MasterKeyPanel.IsVisible = ChangeMasterKeyToggle.IsChecked == true;
    }

    private void NewPassword_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var pwd = NewPasswordBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(pwd))
        {
            PwdStrengthBar.Value = 0;
            PwdStrengthText.Text = L("Str_EnterANewMasterPassword2");
            return;
        }
        var strength = PasswordGenerator.EvaluateStrength(pwd);
        PwdStrengthBar.Value = Math.Clamp(strength.EntropyBits / 120.0, 0.05, 1.0);
        PwdStrengthText.Text = FormatPasswordStrength(strength);
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
            _newKeyFilePath = path;
            KeyFileBox.Text = Path.GetFileName(path);
        }
    }

    private async void GenerateKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{_database.Meta.DatabaseName}.keyx",
            DefaultExtension = "keyx",
            FileTypeChoices = [new FilePickerFileType(L("Str_KeePassXMLV20KeyFile")) { Patterns = ["*.keyx"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var xml = CompositeKey.GenerateXmlKeyFileV2();
        await File.WriteAllTextAsync(path, xml);
        _newKeyFilePath = path;
        KeyFileBox.Text = Path.GetFileName(path);
    }

    private void ClearKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        _newKeyFilePath = string.Empty;
        KeyFileBox.Text = string.Empty;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        CompositeKey? newKey = null;

        if (ChangeMasterKeyToggle.IsChecked == true)
        {
            var pwd = NewPasswordBox.Text ?? string.Empty;
            var confirm = ConfirmPasswordBox.Text ?? string.Empty;
            if (pwd != confirm)
            {
                ErrorText.Text = L("Str_TheNewMasterPasswordsDoNotMatch");
                ErrorText.IsVisible = true;
                return;
            }
            bool useWua = OperatingSystem.IsWindows() && UseWindowsUserAccountCheck.IsChecked == true;
            if (string.IsNullOrEmpty(pwd) && string.IsNullOrWhiteSpace(_newKeyFilePath) && !useWua)
            {
                ErrorText.Text = L("Str_EnterAMasterPasswordAKeyFile");
                ErrorText.IsVisible = true;
                return;
            }
            newKey = CompositeKey.FromPasswordAndKeyFile(pwd, _newKeyFilePath, useWua);
            _database.Meta.MasterKeyChanged = DateTimeOffset.UtcNow;
        }

        // 应用常规与加密参数
        var newName = (DbNameBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(newName) && newName != _database.Meta.DatabaseName)
        {
            _database.Meta.DatabaseName = newName;
            _database.Meta.DatabaseNameChanged = DateTimeOffset.UtcNow;
            _database.RootGroup.Name = newName;
        }

        var newDesc = (DbDescBox.Text ?? string.Empty).Trim();
        if (newDesc != _database.Meta.DatabaseDescription)
        {
            _database.Meta.DatabaseDescription = newDesc;
            _database.Meta.DatabaseDescriptionChanged = DateTimeOffset.UtcNow;
        }

        var newDefaultUser = (DefaultUserBox.Text ?? string.Empty).Trim();
        if (newDefaultUser != _database.Meta.DefaultUserName)
        {
            _database.Meta.DefaultUserName = newDefaultUser;
            _database.Meta.DefaultUserNameChanged = DateTimeOffset.UtcNow;
        }

        _database.Cipher = CipherCombo.SelectedIndex switch
        {
            1 => KdbxCipherAlgorithm.ChaCha20,
            2 => KdbxCipherAlgorithm.Twofish,
            _ => KdbxCipherAlgorithm.Aes256
        };

        _database.Kdf = KdfCombo.SelectedIndex switch
        {
            1 => KdbxKdfAlgorithm.Argon2d,
            2 => KdbxKdfAlgorithm.AesKdf,
            _ => KdbxKdfAlgorithm.Argon2id
        };

        _database.Compression = CompressionCombo.SelectedIndex == 1 ? KdbxCompression.None : KdbxCompression.GZip;
        _database.KdfIterations = (ulong)Math.Max(1, (int)(ArgonIterationsNum.Value ?? 3));
        _database.KdfMemoryBytes = (ulong)Math.Max(8, (int)(ArgonMemoryNum.Value ?? 64)) * 1024UL * 1024UL;
        _database.KdfParallelism = (uint)Math.Max(1, (int)(ArgonParallelismNum.Value ?? 2));
        _database.AesKdfRounds = (ulong)Math.Max(10_000, (long)(AesRoundsNum.Value ?? 600_000));

        _database.Meta.RecycleBinEnabled = RecycleBinToggle.IsChecked == true;
        _database.Meta.HistoryMaxItems = Math.Max(0, (int)(HistoryMaxItemsNum.Value ?? 10));
        _database.IsModified = true;

        Close(new DatabaseSettingsResult(true, newKey, string.IsNullOrWhiteSpace(_newKeyFilePath) ? null : _newKeyFilePath));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
        => Close(new DatabaseSettingsResult(false, null, null));

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or TextBox or ComboBox or NumericUpDown or ToggleSwitch) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
