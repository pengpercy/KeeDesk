using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeeDesk.Core.Kdbx;
using KeeDesk.Helpers;

namespace KeeDesk.Views;

/// <summary>
/// 对标原版 KeePass 2.x FieldRefForm 的可视化跨条目字段引用向导。
/// </summary>
public partial class FieldReferenceWindow : Window
{
    private readonly List<KdbxEntry> _allEntries;
    private List<KdbxEntry> _filteredEntries;

    public FieldReferenceWindow() : this(KdbxDatabase.CreateDefault(), 'P') { }

    public FieldReferenceWindow(KdbxDatabase db, char defaultWantedField = 'P')
    {
        InitializeComponent();
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 40);

        _allEntries = db.EnumerateAllEntries(false).OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
        _filteredEntries = new List<KdbxEntry>(_allEntries);

        WantedFieldCombo.SelectedIndex = defaultWantedField switch
        {
            'U' => 1,
            'T' => 2,
            'A' => 3,
            'N' => 4,
            _ => 0
        };
        IdentifyByCombo.SelectedIndex = 0;

        RefreshList();
    }

    private void RefreshList()
    {
        var q = (FilterBox.Text ?? string.Empty).Trim();
        _filteredEntries = string.IsNullOrEmpty(q)
            ? new List<KdbxEntry>(_allEntries)
            : _allEntries.Where(e =>
                e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.UserName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        EntryListBox.ItemsSource = _filteredEntries
            .Select(e => string.IsNullOrWhiteSpace(e.UserName) ? e.Title : $"{e.Title}  ({e.UserName})")
            .ToList();

        if (_filteredEntries.Count > 0) EntryListBox.SelectedIndex = 0;
        UpdatePreview();
    }

    private void FilterBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshList();

    private void Selection_Changed(object? sender, SelectionChangedEventArgs e) => UpdatePreview();

    private string BuildReference()
    {
        if (EntryListBox.SelectedIndex < 0 || EntryListBox.SelectedIndex >= _filteredEntries.Count)
            return string.Empty;

        var entry = _filteredEntries[EntryListBox.SelectedIndex];
        var wanted = (WantedFieldCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "P";
        var idBy = (IdentifyByCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "I";

        string searchVal = idBy switch
        {
            "T" => entry.Title,
            "U" => entry.UserName,
            _ => entry.Uuid.ToString("N").ToUpperInvariant()
        };

        return $"{{REF:{wanted}@{idBy}:{searchVal}}}";
    }

    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        var expr = BuildReference();
        PreviewText.Text = string.IsNullOrEmpty(expr) ? "请先在上方选择一个目标条目" : expr;
    }

    private void Insert_Click(object? sender, RoutedEventArgs e)
    {
        var expr = BuildReference();
        Close(string.IsNullOrEmpty(expr) ? null : expr);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or TextBox or ComboBox or ListBox) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
