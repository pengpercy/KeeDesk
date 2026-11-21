using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeePassModern.Core.Kdbx;
using KeePassModern.Helpers;

namespace KeePassModern.Views;

/// <summary>
/// 选择目标分组以移动条目的对话框。
/// </summary>
public partial class MoveToGroupWindow : Window
{
    private readonly List<KdbxGroup> _groups = new();

    public MoveToGroupWindow() : this(KdbxDatabase.CreateDefault(), new KdbxEntry()) { }

    public MoveToGroupWindow(KdbxDatabase db, KdbxEntry entry)
    {
        InitializeComponent();
        EntryTitleLabel.Text = $"条目：{entry.Title}";
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 40);

        BuildFlatGroups(db.RootGroup, db.Meta.RecycleBinUuid, 0);
        GroupListBox.ItemsSource = _groups.Select((g, idx) =>
        {
            int depth = GetDepth(g);
            var indent = new string(' ', depth * 4);
            return $"{indent}{g.Name}  ({g.Entries.Count})";
        }).ToList();

        int currentIdx = entry.Parent is not null ? _groups.IndexOf(entry.Parent) : 0;
        if (currentIdx >= 0) GroupListBox.SelectedIndex = currentIdx;
    }

    private void BuildFlatGroups(KdbxGroup group, Guid recycleBinUuid, int depth)
    {
        if (recycleBinUuid != Guid.Empty && group.Uuid == recycleBinUuid) return;
        if (depth > 0) _groups.Add(group);
        foreach (var sub in group.Groups)
            BuildFlatGroups(sub, recycleBinUuid, depth + 1);
    }

    private static int GetDepth(KdbxGroup g)
    {
        int d = 0;
        var p = g.Parent;
        while (p?.Parent is not null)
        {
            d++;
            p = p.Parent;
        }
        return d;
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (GroupListBox.SelectedIndex >= 0 && GroupListBox.SelectedIndex < _groups.Count)
            Close(_groups[GroupListBox.SelectedIndex]);
        else
            Close(null);
    }

    private void GroupListBox_DoubleTapped(object? sender, TappedEventArgs e) => Confirm_Click(sender, e);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or ListBox) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
