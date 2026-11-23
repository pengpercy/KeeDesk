using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.XPath;
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
/// 高级查找弹窗，支持简单表达式、正则、XPath 和加密密码库中的搜索配置。
/// </summary>
public partial class AdvancedSearchWindow : Window
{
    public AdvancedSearchWindow()
    {
        InitializeComponent();
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 40);
        Opened += (_, _) => QueryBox.Focus();
        ModeBox.SelectionChanged += (_, _) => UpdateMode();
        ProfileBox.SelectionChanged += Profile_Changed;
        UpdateMode();
    }

    private KdbxDatabase? _database;
    private Func<SearchProfile[], bool>? _saveProfiles;
    private SearchProfile[] _profiles = [];

    internal AdvancedSearchWindow(KdbxDatabase database, Func<SearchProfile[], bool> saveProfiles) : this()
    {
        _database = database;
        _saveProfiles = saveProfiles;
        try
        {
            _profiles = SearchProfileStore.Load(database);
            RefreshProfiles();
        }
        catch (JsonException)
        {
            ProfilesPanel.IsEnabled = false;
            ShowProfileError("Str_CouldNotLoadSearchProfiles");
        }
    }

    private EntrySearchParameters ReadParameters() => new()
    {
        Query = QueryBox.Text?.Trim() ?? string.Empty,
        UseRegex = ModeBox.SelectedIndex == 1,
        UseXPath = ModeBox.SelectedIndex == 2,
        CaseSensitive = ChkCase.IsChecked == true,
        SearchTitles = ChkTitle.IsChecked == true,
        SearchUserNames = ChkUser.IsChecked == true,
        SearchPasswords = ChkPass.IsChecked == true,
        SearchUrls = ChkUrl.IsChecked == true,
        SearchNotes = ChkNotes.IsChecked == true,
        SearchTags = ChkTags.IsChecked == true,
        SearchCustomFields = ChkCustom.IsChecked == true,
        SearchUuid = ChkUuid.IsChecked == true,
        SearchHistory = ChkHistory.IsChecked == true,
        ExcludeExpired = ChkExcludeExpired.IsChecked == true,
        IncludeRecycleBin = ChkIncludeBin.IsChecked == true
    };

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        ErrorText.IsVisible = false;
        var parameters = ReadParameters();
        try
        {
            Close((parameters.Query, EntrySearchService.Search(_database, parameters)));
        }
        catch (RegexMatchTimeoutException)
        {
            ShowSearchError("Str_SearchExpressionTimedOut");
        }
        catch (XPathException ex) when (ex.InnerException is RegexMatchTimeoutException)
        {
            ShowSearchError("Str_SearchExpressionTimedOut");
        }
        catch (Exception ex) when (ex is ArgumentException or XPathException)
        {
            ShowSearchError(parameters.UseXPath ? "Str_InvalidXPathSearch" : "Str_InvalidRegexSearch");
        }
    }

    private void UpdateMode()
    {
        QueryBox.Classes.Set("regexSearch", ModeBox.SelectedIndex == 1);
        QueryBox.Classes.Set("xpathSearch", ModeBox.SelectedIndex == 2);
        ChkCase.IsEnabled = ChkUuid.IsEnabled = ModeBox.SelectedIndex != 2;
        ErrorText.IsVisible = false;
    }

    private void RefreshProfiles(int selectedIndex = -1)
    {
        ProfileBox.ItemsSource = _profiles.Select(profile => profile.Name).ToArray();
        ProfileBox.SelectedIndex = selectedIndex;
        DeleteProfileButton.IsEnabled = selectedIndex >= 0;
    }

    private void Profile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        int index = ProfileBox.SelectedIndex;
        DeleteProfileButton.IsEnabled = index >= 0;
        ProfileErrorText.IsVisible = false;
        ProfileEditor.IsVisible = false;
        if (index < 0 || index >= _profiles.Length) return;

        var parameters = _profiles[index].Parameters;
        QueryBox.Text = parameters.Query;
        ModeBox.SelectedIndex = parameters.UseXPath ? 2 : parameters.UseRegex ? 1 : 0;
        ChkCase.IsChecked = parameters.CaseSensitive;
        ChkTitle.IsChecked = parameters.SearchTitles;
        ChkUser.IsChecked = parameters.SearchUserNames;
        ChkPass.IsChecked = parameters.SearchPasswords;
        ChkUrl.IsChecked = parameters.SearchUrls;
        ChkNotes.IsChecked = parameters.SearchNotes;
        ChkTags.IsChecked = parameters.SearchTags;
        ChkCustom.IsChecked = parameters.SearchCustomFields;
        ChkUuid.IsChecked = parameters.SearchUuid;
        ChkHistory.IsChecked = parameters.SearchHistory;
        ChkExcludeExpired.IsChecked = parameters.ExcludeExpired;
        ChkIncludeBin.IsChecked = parameters.IncludeRecycleBin;
        ErrorText.IsVisible = false;
    }

    private void ShowSaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        ProfileErrorText.IsVisible = false;
        ProfileNameBox.Text = ProfileBox.SelectedItem as string;
        ProfileEditor.IsVisible = true;
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    private void CancelProfile_Click(object? sender, RoutedEventArgs e)
    {
        ProfileEditor.IsVisible = false;
        ProfileErrorText.IsVisible = false;
    }

    private void ProfileNameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SaveProfile_Click(sender, e);
        else if (e.Key == Key.Escape)
            CancelProfile_Click(sender, e);
        else
            return;

        e.Handled = true;
    }

    private void SaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowProfileError("Str_EnterSearchProfileName");
            return;
        }

        var updated = _profiles.Where(profile => !string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
            .Append(new SearchProfile(name, ReadParameters()))
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (SaveProfiles(updated))
            RefreshProfiles(Array.FindIndex(_profiles, profile => profile.Name == name));
    }

    private void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        int index = ProfileBox.SelectedIndex;
        if (index >= 0 && SaveProfiles(_profiles.Where((_, position) => position != index).ToArray()))
            RefreshProfiles();
    }

    private bool SaveProfiles(SearchProfile[] profiles)
    {
        if (_saveProfiles?.Invoke(profiles) != true)
        {
            ShowProfileError("Str_CouldNotSaveSearchProfiles");
            return false;
        }

        _profiles = profiles;
        ProfileEditor.IsVisible = false;
        ProfileErrorText.IsVisible = false;
        return true;
    }

    private void ShowProfileError(string key)
    {
        ProfileErrorText.Text = L(key);
        ProfileErrorText.IsVisible = true;
    }

    private void ShowSearchError(string key)
    {
        ErrorText.Text = L(key);
        ErrorText.IsVisible = true;
        QueryBox.Focus();
        QueryBox.BringIntoView();
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
