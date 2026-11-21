using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeePassModern.Core.Kdbx;
using KeePassModern.Core.Services;
using KeePassModern.Helpers;

namespace KeePassModern.Views;

/// <summary>
/// 密码库维护工具窗口，对标原版 KeePass 2.x DatabaseOperationsForm。
/// </summary>
public partial class DatabaseMaintenanceWindow : Window
{
    private readonly KdbxDatabase _database;
    private bool _changed;

    public DatabaseMaintenanceWindow() : this(KdbxDatabase.CreateDefault()) { }

    public DatabaseMaintenanceWindow(KdbxDatabase database)
    {
        InitializeComponent();
        _database = database;
        HistoryDaysNum.Value = Math.Clamp(_database.Meta.MaintenanceHistoryDays, 0, 3650);
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 44);
        RefreshStats();
    }

    private void RefreshStats()
    {
        var s = DatabaseMaintenanceService.GetStats(_database);
        StatEntries.Text = s.TotalEntries.ToString();
        StatHistory.Text = s.TotalHistoryItems.ToString();
        StatDeleted.Text = s.DeletedObjectsCount.ToString();
        StatAttachments.Text = s.TotalAttachmentBytes < 1024
            ? $"{s.TotalAttachmentBytes} B"
            : $"{s.TotalAttachmentBytes / 1024.0:0.0} KB";
        UnusedIconsDesc.Text = $"当前有 {s.UnusedCustomIconsCount} 个未被任何条目或分组引用的自定义图标";
    }

    private void ShowResult(string message)
    {
        ResultText.Text = message;
        ResultText.IsVisible = true;
        RefreshStats();
    }

    private void PurgeHistory_Click(object? sender, RoutedEventArgs e)
    {
        int days = (int)(HistoryDaysNum.Value ?? 365);
        int count = DatabaseMaintenanceService.PurgeHistoryOlderThan(_database, days);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? $"已清理 {count} 条历史版本快照。" : "没有符合条件的过期历史快照。");
    }

    private void DeleteDuplicates_Click(object? sender, RoutedEventArgs e)
    {
        int count = DatabaseMaintenanceService.DeleteDuplicateEntries(_database);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? $"已清理 {count} 条完全重复的条目。" : "未发现完全重复的条目。");
    }

    private void DeleteEmptyGroups_Click(object? sender, RoutedEventArgs e)
    {
        int count = DatabaseMaintenanceService.DeleteEmptyGroups(_database);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? $"已移除 {count} 个空分组。" : "当前没有空分组。");
    }

    private void ClearDeletedObjects_Click(object? sender, RoutedEventArgs e)
    {
        int count = DatabaseMaintenanceService.ClearDeletedObjects(_database);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? $"已清空 {count} 条已删除对象追踪记录。" : "已删除对象记录为空。");
    }

    private void DeleteUnusedIcons_Click(object? sender, RoutedEventArgs e)
    {
        int count = DatabaseMaintenanceService.DeleteUnusedCustomIcons(_database);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? $"已清理 {count} 个未使用的自定义图标。" : "没有未使用的自定义图标。");
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(_changed);

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or NumericUpDown) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
