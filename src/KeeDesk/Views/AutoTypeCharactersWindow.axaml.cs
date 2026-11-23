using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

public partial class AutoTypeCharactersWindow : Window
{
    private readonly AutoTypePickCharactersStep? _request;
    private readonly int _length;

    public AutoTypeCharactersWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Positions.Focus();
    }

    internal AutoTypeCharactersWindow(AutoTypePickCharactersStep request) : this()
    {
        _request = request;
        var characters = request.GetCharacters();
        _length = characters.Length;
        FieldHint.Text = request.Count is { } count
            ? L("Str_AutoTypePickCountFormat", request.Field, count, _length)
            : L("Str_AutoTypePickAnyFormat", request.Field, _length);
        PreviewScroll.IsVisible = !request.Hide;
        if (!request.Hide)
            CharacterPreview.Text = string.Join("  ", characters.Select((character, index) => $"{index + 1}·{character}"));
    }

    private void Select_Click(object? sender, RoutedEventArgs e)
    {
        var values = (Positions.Text ?? string.Empty).Split([',', '，', ' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var positions = new List<int>(values.Length);
        foreach (var value in values)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int position) ||
                position < 1 || position > _length)
            {
                ErrorText.IsVisible = true;
                return;
            }
            positions.Add(position);
        }
        if (positions.Count == 0 || (_request?.Count is { } count && positions.Count != count))
        {
            ErrorText.IsVisible = true;
            return;
        }

        Close(positions);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
