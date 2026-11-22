using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using KeeDesk.Helpers;
using KeeDesk.ViewModels;

namespace KeeDesk.Views;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel? _vm;
    private bool _loading = true;

    public SettingsWindow() : this(null, 0) { }

    public SettingsWindow(MainWindowViewModel? vm, int initialTabIndex = 0)
    {
        InitializeComponent();
        DesktopSettingsChrome.IsVisible = !DesktopWindowChrome.ButtonsOnLeft;
        MacTitleBarInsets.AttachTrafficLightsCentering(this, ExtendClientAreaTitleBarHeightHint);
        InitializeShortcutReference();
        SwitchPage(initialTabIndex);
        _vm = vm;
        AboutActions.DataContext = vm;
        CheckUpdateIcon.Classes.Set("animateUpdate", !SystemMotionPreferences.ReduceMotion);
        if (vm is not null)
        {
            var s = vm.SettingsService.Settings;
            FollowSystemAccentCheck.IsChecked = string.Equals(s.AccentMode, "System", StringComparison.OrdinalIgnoreCase);
            if (Color.TryParse(s.CustomAccentHex, out var c))
                CustomAccentPicker.Color = c;
            ClipClearNumeric.Value = s.ClipboardClearSeconds;
            AutoLockEnabledSwitch.IsChecked = s.AutoLockIdleEnabled;
            AutoLockNumeric.Value = s.AutoLockIdleMinutes;
            AutoLockNumeric.IsEnabled = s.AutoLockIdleEnabled;
            LanguageCombo.SelectedIndex = string.Equals(s.Language, "en-US", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            ThemeCombo.SelectedIndex = s.ThemeMode switch { "Light" => 1, "Dark" => 2, _ => 0 };
            DensityCombo.SelectedIndex = s.UiDensity switch { "Compact" => 0, "Comfortable" => 2, _ => 1 };
            RefreshAccentRings(s.AccentMode);
        }
        AboutVersionText.Text = $"KeeDesk v{KeeDesk.Services.AppVersionProvider.GetCurrentVersion()}";
        _loading = false;
    }

    private void InitializeShortcutReference()
    {
        bool macOS = OperatingSystem.IsMacOS();
        bool hasGlobalAutoType = macOS || OperatingSystem.IsWindows();
        GlobalAutoTypeHint.IsVisible = hasGlobalAutoType;
        GlobalAutoTypeKeys.IsVisible = hasGlobalAutoType;
        ManualAutoTypeHint.IsVisible = !hasGlobalAutoType;
        if (hasGlobalAutoType)
            GlobalAutoTypeKeys.ItemsSource = macOS ? new[] { "⌃", "⌥", "A" } : new[] { "Ctrl", "Alt", "A" };

        string modifier = macOS ? "⌘" : "Ctrl";
        SearchShortcutKeys.ItemsSource = new[] { modifier, "K" };
        NewEntryShortcutKeys.ItemsSource = new[] { modifier, "N" };
        SaveShortcutKeys.ItemsSource = new[] { modifier, "S" };
        LockShortcutKeys.ItemsSource = new[] { modifier, "L" };
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && !BackButton.IsPointerOver)
            BeginMoveDrag(e);
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => Close();

    private void NavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out int idx)) return;
        SwitchPage(idx);
    }

    private void SwitchPage(int idx)
    {
        PageAppearance.IsVisible = idx == 0;
        PageSecurity.IsVisible = idx == 1;
        PageAutoType.IsVisible = idx == 2;
        PageData.IsVisible = idx == 3;
        PageAbout.IsVisible = idx == 4;

        Ind0.IsVisible = idx == 0;
        Ind1.IsVisible = idx == 1;
        Ind2.IsVisible = idx == 2;
        Ind3.IsVisible = idx == 3;
        Ind4.IsVisible = idx == 4;
        NavAppearance.Classes.Set("active", idx == 0);
        NavSecurity.Classes.Set("active", idx == 1);
        NavAutoType.Classes.Set("active", idx == 2);
        NavData.Classes.Set("active", idx == 3);
        NavAbout.Classes.Set("active", idx == 4);
    }

    private void LanguageCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm is null) return;
        var lang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-CN";
        _vm.SettingsService.Settings.Language = lang;
        _vm.SettingsService.Save();
        KeeDesk.Services.LocalizationService.Apply(lang);
        _vm.ShowToast(lang == "en-US" ? "Switched UI language to English" : "已切换界面语言为简体中文");
    }

    private void ThemeCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm is null || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        _vm.SetThemeModeCommand.Execute(mode);
    }

    private void FollowSystemAccent_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _vm is null) return;
        var mode = FollowSystemAccentCheck.IsChecked == true ? "System" : "Emerald";
        _vm.SettingsService.Settings.AccentMode = mode;
        _vm.SettingsService.Save();
        ThemeAccentService.ApplyAccent(mode, _vm.SettingsService.Settings.CustomAccentHex);
        RefreshAccentRings(mode);
    }

    private void AccentPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (_loading || _vm is null || sender is not Button btn) return;
        var preset = btn.Tag?.ToString() ?? "Emerald";
        _loading = true;
        FollowSystemAccentCheck.IsChecked = false;
        _loading = false;
        _vm.SettingsService.Settings.AccentMode = preset;
        _vm.SettingsService.Save();
        ThemeAccentService.ApplyAccent(preset, _vm.SettingsService.Settings.CustomAccentHex);
        RefreshAccentRings(preset);
    }

    private void CustomAccent_Changed(object? sender, ColorChangedEventArgs e)
    {
        if (_loading || _vm is null) return;
        _loading = true;
        FollowSystemAccentCheck.IsChecked = false;
        _loading = false;
        var hex = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
        _vm.SettingsService.Settings.AccentMode = "Custom";
        _vm.SettingsService.Settings.CustomAccentHex = hex;
        _vm.SettingsService.Save();
        ThemeAccentService.ApplyAccent("Custom", hex);
        RefreshAccentRings("Custom");
    }

    private void DensityCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm is null || DensityCombo.SelectedItem is not ComboBoxItem { Tag: string density }) return;
        _vm.SettingsService.Settings.UiDensity = density;
        _vm.SettingsService.Save();
        DensityService.Apply(density);
        _vm.CurrentDensityLabel = density switch
        {
            "Compact" => "紧凑",
            "Comfortable" => "宽松",
            _ => "标准"
        };
    }

    private void RefreshAccentRings(string accentMode)
    {
        var activeBrush = new SolidColorBrush(Color.Parse(ThemeAccentService.DefaultAccent));
        RingEmerald.BorderBrush = string.Equals(accentMode, "Emerald", StringComparison.OrdinalIgnoreCase) ? activeBrush : Brushes.Transparent;
        RingClassicBlue.BorderBrush = string.Equals(accentMode, "ClassicBlue", StringComparison.OrdinalIgnoreCase) ? new SolidColorBrush(Color.Parse("#3B82F6")) : Brushes.Transparent;
        RingIndigo.BorderBrush = string.Equals(accentMode, "Indigo", StringComparison.OrdinalIgnoreCase) ? new SolidColorBrush(Color.Parse("#6366F1")) : Brushes.Transparent;
        RingAmber.BorderBrush = string.Equals(accentMode, "Amber", StringComparison.OrdinalIgnoreCase) ? new SolidColorBrush(Color.Parse("#D97706")) : Brushes.Transparent;
        RingRose.BorderBrush = string.Equals(accentMode, "Rose", StringComparison.OrdinalIgnoreCase) ? new SolidColorBrush(Color.Parse("#E11D48")) : Brushes.Transparent;
        RingSlate.BorderBrush = string.Equals(accentMode, "Slate", StringComparison.OrdinalIgnoreCase) ? new SolidColorBrush(Color.Parse("#64748B")) : Brushes.Transparent;
    }

    private void AutoLockEnabled_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _vm is null) return;
        bool enabled = AutoLockEnabledSwitch.IsChecked == true;
        AutoLockNumeric.IsEnabled = enabled;
        _vm.SettingsService.Settings.AutoLockIdleEnabled = enabled;
        _vm.SettingsService.Save();
        _vm.RecordUserActivity();
    }

    private void SecurityChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loading || _vm is null) return;
        _vm.SettingsService.Settings.ClipboardClearSeconds = (int)(ClipClearNumeric.Value ?? 15);
        _vm.SettingsService.Settings.AutoLockIdleMinutes = (int)(AutoLockNumeric.Value ?? 10);
        _vm.SettingsService.Save();
        _vm.RecordUserActivity();
    }

    private async void ImportCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ImportCsvCommand.ExecuteAsync(null);
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ExportCsvCommand.ExecuteAsync(null);
    }

    private async void ExportEmergency_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ExportEmergencySheetCommand.ExecuteAsync(null);
    }

    private void OpenGitHub_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/pengpercy/KeeDesk") { UseShellExecute = true });
        }
        catch { }
    }
}
