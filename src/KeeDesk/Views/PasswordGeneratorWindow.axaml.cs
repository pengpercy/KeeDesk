using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

public partial class PasswordGeneratorWindow : Window
{
    private string _currentPassword = string.Empty;

    public PasswordGeneratorWindow() : this(false) { }

    public PasswordGeneratorWindow(bool fillEntry)
    {
        InitializeComponent();
        if (fillEntry)
        {
            CancelButton.Content = L("Str_Cancel");
            AcceptButton.Content = L("Str_UseThisPassword");
        }
        KeeDesk.Helpers.MacTitleBarInsets.AttachTrafficLightsCentering(this, 42);
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
        LengthLabel.Text = L("Str_CharactersFormat", len);
        int words = (int)WordSlider.Value;
        WordCountLabel.Text = L("Str_WordsFormat", words);

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

        if (ModeTabs.SelectedIndex == 3)
        {
            var batch = Enumerable.Range(0, 10)
                .Select(_ => PasswordGenerator.GenerateFromCharset(opts))
                .ToList();
            BatchListBox.Text = string.Join(Environment.NewLine, batch);
            _currentPassword = BatchListBox.Text;
            PreviewText.Text = batch[0];
            var res0 = PasswordGenerator.EvaluateStrength(batch[0]);
            StrengthBar.Value = Math.Clamp(res0.EntropyBits / 120.0, 0.05, 1.0);
            StrengthText.Text = L("Str_10PasswordsAroundBitsEachFormat", res0.EntropyBits);
            return;
        }

        _currentPassword = ModeTabs.SelectedIndex switch
        {
            1 => PasswordGenerator.GeneratePassphrase(opts),
            2 => PasswordGenerator.GenerateFromPattern(opts.Pattern, opts.ExcludeLookAlike),
            _ => PasswordGenerator.GenerateFromCharset(opts)
        };

        PreviewText.Text = _currentPassword;
        var res = PasswordGenerator.EvaluateStrength(_currentPassword);
        StrengthBar.Value = Math.Clamp(res.EntropyBits / 120.0, 0.05, 1.0);
        StrengthText.Text = FormatPasswordStrength(res);
    }

    private void Regenerate_Click(object? sender, RoutedEventArgs e) => Regenerate();
    private void ModeChanged(object? sender, SelectionChangedEventArgs e) => Regenerate();
    private void CheckChanged(object? sender, RoutedEventArgs e) => Regenerate();
    private void PatternChanged(object? sender, TextChangedEventArgs e) => Regenerate();
    private void SliderChanged(object? sender, RangeBaseValueChangedEventArgs e) => Regenerate();
    private void Accept_Click(object? sender, RoutedEventArgs e) => Close(_currentPassword);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
