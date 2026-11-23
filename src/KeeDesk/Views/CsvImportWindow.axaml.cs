using Avalonia.Controls;
using Avalonia.Interactivity;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

public sealed record CsvImportSelection(CsvImportDocument Document, CsvImportField[] Mapping, bool HasHeader);

public partial class CsvImportWindow : Window
{
    private string _content = string.Empty;
    private CsvImportDocument? _document;
    private readonly List<ComboBox> _selectors = [];
    private bool _ready;

    public CsvImportWindow()
    {
        InitializeComponent();
        _ready = true;
        UpdatePreview();
    }

    internal CsvImportWindow(string content) : this()
    {
        _content = content;
        LoadMapping();
    }

    private bool HasHeader => HeaderCheck?.IsChecked == true;

    private void Layout_Changed(object? sender, RoutedEventArgs e)
    {
        if (_ready)
            LoadMapping();
    }

    private void LoadMapping()
    {
        _selectors.Clear();
        MappingRows.Children.Clear();
        char? delimiter = DelimiterBox.SelectedIndex switch { 1 => ',', 2 => ';', 3 => '\t', _ => null };
        try { _document = CsvImportDocument.Parse(_content, delimiter); }
        catch (FormatException)
        {
            _document = null;
            UpdatePreview();
            ErrorText.Text = L("Str_CsvInvalidFormat");
            ErrorText.IsVisible = true;
            return;
        }

        var mapping = _document.GuessMapping(HasHeader);
        string[] labels = ["Str_CsvSkipColumn", "Str_Groups", "Str_Title", "Str_UserName", "Str_Password",
            "Str_URL", "Str_Notes", "Str_CsvTotpSeed", "Str_Tags", "Str_Favorite"];
        var options = labels.Select(key => L(key)).ToArray();
        for (int i = 0; i < mapping.Length; i++)
        {
            var caption = HasHeader ? _document.Rows[0].ElementAtOrDefault(i) : null;
            if (string.IsNullOrWhiteSpace(caption)) caption = L("Str_CsvColumnFormat", i + 1);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,192"), ColumnSpacing = 12 };
            row.Children.Add(new TextBlock
            {
                Text = caption, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            });
            var selector = new ComboBox
            {
                ItemsSource = options, SelectedIndex = (int)mapping[i], Height = 32,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            Avalonia.Automation.AutomationProperties.SetName(selector, caption);
            selector.SelectionChanged += (_, _) => UpdatePreview();
            Grid.SetColumn(selector, 1);
            row.Children.Add(selector);
            MappingRows.Children.Add(row);
            _selectors.Add(selector);
        }
        UpdatePreview();
    }

    private CsvImportField[] ReadMapping() =>
        _selectors.Select(selector => (CsvImportField)Math.Max(selector.SelectedIndex, 0)).ToArray();

    private void UpdatePreview()
    {
        var mapping = ReadMapping();
        var assigned = mapping.Where(field => field != CsvImportField.Ignore).ToArray();
        bool mappingValid = assigned.Distinct().Count() == assigned.Length;
        var rows = _document?.Rows.Skip(HasHeader ? 1 : 0)
            .Where(row => assigned.Any(field => !string.IsNullOrWhiteSpace(CsvImportDocument.Cell(row, mapping, field))))
            .ToArray() ?? [];
        PreviewHeading.Text = L("Str_CsvPreviewFormat", rows.Length);
        PreviewText.Text = string.Join(Environment.NewLine, rows.Take(4).Select((row, index) =>
        {
            var visible = new[] { CsvImportField.Group, CsvImportField.Title, CsvImportField.UserName }
                .Select(field => CsvImportDocument.Cell(row, mapping, field))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Length > 60 ? value[..60] + "…" : value);
            var sample = string.Join(" · ", visible);
            return sample.Length > 0 ? sample : L("Str_CsvPreviewEntryFormat", index + 1);
        }));
        ImportButton.IsEnabled = mappingValid && rows.Length > 0;
        ErrorText.Text = L("Str_CsvDuplicateMapping");
        ErrorText.IsVisible = !mappingValid;
    }

    private void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (_document is not null && ImportButton.IsEnabled)
            Close(new CsvImportSelection(_document, ReadMapping(), HasHeader));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
