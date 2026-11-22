using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using KeeDesk.Helpers;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

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
        UnusedIconsDesc.Text = L("Str_UnusedCustomIconsFormat", s.UnusedCustomIconsCount);
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
        ShowResult(count > 0 ? L("Str_RemovedHistorySnapshotsFormat", count) : L("Str_NoHistorySnapshotsMatchTheCleanupCriteria"));
    }

    private void DeleteDuplicates_Click(object? sender, RoutedEventArgs e)
    {
        int count = DatabaseMaintenanceService.DeleteDuplicateEntries(_database);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? L("Str_RemovedExactDuplicateEntriesFormat", count) : L("Str_NoExactDuplicateEntries"));
    }

    private void DeleteEmptyGroups_Click(object? sender, RoutedEventArgs e)
    {
        int count = DatabaseMaintenanceService.DeleteEmptyGroups(_database);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? L("Str_RemovedEmptyGroupsFormat", count) : L("Str_NoEmptyGroups"));
    }

    private void ClearDeletedObjects_Click(object? sender, RoutedEventArgs e)
    {
        int count = DatabaseMaintenanceService.ClearDeletedObjects(_database);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? L("Str_ClearedDeletedObjectRecordsFormat", count) : L("Str_NoDeletedObjectRecords"));
    }

    private void DeleteUnusedIcons_Click(object? sender, RoutedEventArgs e)
    {
        int count = DatabaseMaintenanceService.DeleteUnusedCustomIcons(_database);
        if (count > 0) _changed = true;
        ShowResult(count > 0 ? L("Str_RemovedUnusedCustomIconsFormat", count) : L("Str_NoUnusedCustomIcons"));
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
