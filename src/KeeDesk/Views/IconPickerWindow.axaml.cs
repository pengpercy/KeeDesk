using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using KeeDesk.Core.Kdbx;
using KeeDesk.Helpers;

namespace KeeDesk.Views;

public sealed record IconPickerResult(int IconId, Guid CustomIconUuid);

/// <summary>
/// 对标原版 KeePass 2.x IconPickerForm 的标准/自定义图标选择器。
/// </summary>
public partial class IconPickerWindow : Window
{
    private readonly KdbxDatabase? _database;
    private int _selectedIconId;
    private Guid _selectedCustomIconUuid;

    public IconPickerWindow() : this(null, 0, Guid.Empty, string.Empty) { }

    public IconPickerWindow(KdbxDatabase? db, int currentIconId, Guid currentCustomIconUuid, string suggestedDomain = "")
    {
        InitializeComponent();
        _database = db;
        _selectedIconId = currentIconId;
        _selectedCustomIconUuid = currentCustomIconUuid;
        FetchUrlBox.Text = suggestedDomain;

        MacTitleBarInsets.AttachTrafficLightsCentering(this, 40);
        RenderIcons();
    }

    private async void FetchFavicon_Click(object? sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        var input = (FetchUrlBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(input))
        {
            FetchStatusText.Text = "请输入网站域名（如 github.com）或完整图片 URL。";
            FetchStatusText.IsVisible = true;
            return;
        }

        FetchBtn.IsEnabled = false;
        FetchStatusText.Text = "正在获取图标...";
        FetchStatusText.IsVisible = true;

        try
        {
            bool looksLikeImageUrl = input.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                                     (input.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                      input.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
                                      input.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                      input.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                      input.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));

            byte[]? bytes;
            string name;
            if (looksLikeImageUrl)
            {
                bytes = await KeeDesk.Services.FaviconFetcherService.FetchByImageUrlAsync(input);
                name = Path.GetFileNameWithoutExtension(new Uri(input).AbsolutePath);
            }
            else
            {
                var domain = KeeDesk.Services.FaviconFetcherService.ExtractDomain(input, input) ?? input;
                bytes = await KeeDesk.Services.FaviconFetcherService.FetchByDomainAsync(domain);
                name = domain;
            }

            if (bytes is null)
            {
                FetchStatusText.Text = "未能获取到有效图片，请检查网络或尝试其他图片地址。";
                return;
            }

            _selectedCustomIconUuid = KeeDesk.Services.FaviconFetcherService.AddOrReuseCustomIcon(_database, name, bytes);
            FetchStatusText.Text = $"已成功获取并选中「{name}」图标！";
            RenderIcons();
        }
        finally
        {
            FetchBtn.IsEnabled = true;
        }
    }

    private void RenderIcons()
    {
        StandardIconsPanel.Children.Clear();
        foreach (var item in KeePassIconCatalog.StandardIcons)
        {
            bool isSelected = _selectedCustomIconUuid == Guid.Empty && _selectedIconId == item.IconId;
            var btn = new Button
            {
                Width = 44,
                Height = 44,
                Margin = new Thickness(4),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                BorderBrush = isSelected ? new SolidColorBrush(Color.Parse("#10B981")) : Brushes.Transparent,
                Content = new PathIcon
                {
                    Data = KeePassIconCatalog.GetGeometry(item.IconId),
                    Width = 18,
                    Height = 18
                }
            };
            ToolTip.SetTip(btn, item.Name);
            var id = item.IconId;
            btn.Click += (_, _) =>
            {
                _selectedIconId = id;
                _selectedCustomIconUuid = Guid.Empty;
                RenderIcons();
            };
            StandardIconsPanel.Children.Add(btn);
        }

        CustomIconsPanel.Children.Clear();
        var customIcons = _database?.Meta.CustomIcons ?? [];
        NoCustomIconsHint.IsVisible = customIcons.Count == 0;

        foreach (var icon in customIcons)
        {
            try
            {
                using var ms = new MemoryStream(icon.Data);
                var bmp = new Bitmap(ms);
                bool isSelected = _selectedCustomIconUuid == icon.Uuid;
                var btn = new Button
                {
                    Width = 44,
                    Height = 44,
                    Margin = new Thickness(4),
                    Padding = new Thickness(6),
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    BorderBrush = isSelected ? new SolidColorBrush(Color.Parse("#10B981")) : Brushes.Transparent,
                    Content = new Image
                    {
                        Source = bmp,
                        Width = 24,
                        Height = 24,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                var uuid = icon.Uuid;
                btn.Click += (_, _) =>
                {
                    _selectedCustomIconUuid = uuid;
                    RenderIcons();
                };
                CustomIconsPanel.Children.Add(btn);
            }
            catch { }
        }
    }

    private async void ImportCustomIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG 图标") { Patterns = ["*.png", "*.jpg", "*.ico"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var bytes = await File.ReadAllBytesAsync(path);
        var customIcon = new KdbxCustomIcon
        {
            Uuid = Guid.NewGuid(),
            Name = Path.GetFileNameWithoutExtension(path),
            Data = bytes,
            LastModificationTime = DateTimeOffset.UtcNow
        };
        _database.Meta.CustomIcons.Add(customIcon);
        _database.IsModified = true;
        _selectedCustomIconUuid = customIcon.Uuid;
        RenderIcons();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
        => Close(new IconPickerResult(_selectedIconId, _selectedCustomIconUuid));

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
