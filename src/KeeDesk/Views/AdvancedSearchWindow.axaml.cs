using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeeDesk.Core.Services;
using KeeDesk.Helpers;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

/// <summary>
/// 高级查找弹窗，支持正则表达式、区分大小写与按字段范围筛选。
/// </summary>
public partial class AdvancedSearchWindow : Window
{
    public AdvancedSearchWindow()
    {
        InitializeComponent();
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 40);
        Opened += (_, _) => QueryBox.Focus();
    }

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        var q = (QueryBox.Text ?? string.Empty).Trim();
        if (ChkRegex.IsChecked == true && !string.IsNullOrEmpty(q))
        {
            try { _ = new Regex(q); }
            catch (Exception ex)
            {
                ErrorText.Text = L("Str_InvalidRegularExpressionFormat", ex.Message);
                ErrorText.IsVisible = true;
                SearchFormScroll.Offset = default;
                return;
            }
        }

        Close(new EntrySearchParameters
        {
            Query = q,
            UseRegex = ChkRegex.IsChecked == true,
            CaseSensitive = ChkCase.IsChecked == true,
            SearchTitles = ChkTitle.IsChecked == true,
            SearchUserNames = ChkUser.IsChecked == true,
            SearchPasswords = ChkPass.IsChecked == true,
            SearchUrls = ChkUrl.IsChecked == true,
            SearchNotes = ChkNotes.IsChecked == true,
            SearchTags = ChkTags.IsChecked == true,
            SearchCustomFields = ChkCustom.IsChecked == true,
            SearchUuid = ChkUuid.IsChecked == true,
            ExcludeExpired = ChkExcludeExpired.IsChecked == true,
            IncludeRecycleBin = ChkIncludeBin.IsChecked == true
        });
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void QueryBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Search_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or TextBox or CheckBox) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
