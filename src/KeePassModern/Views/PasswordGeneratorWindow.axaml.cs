using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using KeePassModern.Core.Services;

namespace KeePassModern.Views;

/// <summary>
/// 强密码生成器弹窗（对齐 FigCraft 画板 06：随机密码 / 助记词 / KeePass Pattern 模式 + 实时熵值评估）。
/// </summary>
public partial class PasswordGeneratorWindow : Window
{
    private string _currentPassword = string.Empty;

    public PasswordGeneratorWindow()
    {
        InitializeComponent();
        KeePassModern.Helpers.MacTitleBarInsets.AttachTrafficLightsCentering(this, 42);
        Opened += (_, _) => Regenerate();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Regenerate()
    {
        if (ModeTabs is null) return;
        int len = (int)LengthSlider.Value;
        LengthLabel.Text = $"{len} 位";
        int words = (int)WordSlider.Value;
        WordCountLabel.Text = $"{words} 词";

        var opts = new PasswordGeneratorOptions
        {
            Length = len,
            IncludeUppercase = UpperCheck.IsChecked == true,
            IncludeLowercase = LowerCheck.IsChecked == true,
            IncludeDigits = DigitsCheck.IsChecked == true,
            IncludeSpecial = SpecialCheck.IsChecked == true,
            ExcludeLookAlike = ExcludeLookAlikeCheck.IsChecked == true,
            WordCount = words,
            CapitalizeWords = CapWordsCheck.IsChecked == true,
            AppendNumber = AppendNumCheck.IsChecked == true,
            Pattern = PatternBox.Text ?? "uullddss-uullddss"
        };

        _currentPassword = ModeTabs.SelectedIndex switch
        {
            1 => PasswordGenerator.GeneratePassphrase(opts),
            2 => PasswordGenerator.GenerateFromPattern(opts.Pattern, opts.ExcludeLookAlike),
            _ => PasswordGenerator.GenerateFromCharset(opts)
        };

        PreviewText.Text = _currentPassword;
        var res = PasswordGenerator.EvaluateStrength(_currentPassword);
        StrengthBar.Value = Math.Clamp(res.EntropyBits / 120.0, 0.05, 1.0);
        StrengthText.Text = $"{res.Label} · {res.EntropyBits:0} bits";
    }

    private void Regenerate_Click(object? sender, RoutedEventArgs e) => Regenerate();
    private void ModeChanged(object? sender, SelectionChangedEventArgs e) => Regenerate();
    private void CheckChanged(object? sender, RoutedEventArgs e) => Regenerate();
    private void PatternChanged(object? sender, TextChangedEventArgs e) => Regenerate();
    private void SliderChanged(object? sender, RangeBaseValueChangedEventArgs e) => Regenerate();
    private void Accept_Click(object? sender, RoutedEventArgs e) => Close(_currentPassword);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
