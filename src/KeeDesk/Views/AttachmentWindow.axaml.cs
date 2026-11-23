using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using KeeDesk.Core.Crypto;
using KeeDesk.Core.Services;
using SkiaSharp;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

public partial class AttachmentWindow : Window
{
    // Bounds for a single text layout or decoded bitmap in the preview window.
    private const int MaxTextBytes = 1024 * 1024;
    private const int MaxImageBytes = 64 * 1024 * 1024;
    private const int MaxImagePixels = 24_000_000;
    private AttachmentTextDocument? _document;
    private WriteableBitmap? _bitmap;
    private bool _isProtected;
    private bool _editing;

    public AttachmentWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _document = null;
            _editing = false;
            TextPreview.IsUndoEnabled = false;
            TextPreview.Text = string.Empty;
            ImagePreview.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
        };
        AddHandler(KeyDownEvent, Attachment_KeyDown, RoutingStrategies.Tunnel);
    }

    internal AttachmentWindow(string name, ProtectedBinary binary) : this()
    {
        FileNameText.Text = name;
        ToolTip.SetTip(FileNameText, name);
        FileSizeText.Text = L("Str_AttachmentSizeFormat", binary.Length);
        _isProtected = binary.IsProtected;

        var extension = Path.GetExtension(name).ToLowerInvariant();
        bool image = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico";
        bool text = extension is "" or ".txt" or ".md" or ".log" or ".csv" or ".tsv" or ".json" or ".xml"
            or ".ini" or ".conf" or ".cfg" or ".yaml" or ".yml" or ".toml" or ".pem";
        if (!image && !text)
        {
            ShowStatus("Str_AttachmentUnsupportedPreview");
            return;
        }
        if (binary.Length > (image ? MaxImageBytes : MaxTextBytes))
        {
            ShowStatus("Str_AttachmentTooLargeToPreview");
            return;
        }

        var bytes = binary.ReadData();
        try
        {
            if (image)
                LoadImage(bytes);
            else if (AttachmentTextDocument.TryDecode(bytes) is { } document)
            {
                _document = document;
                TextPreview.IsUndoEnabled = false;
                TextPreview.NewLine = document.NewLine;
                TextPreview.Text = document.Text;
                TextPreview.IsVisible = true;
                EditButton.IsVisible = true;
            }
            else
                ShowStatus("Str_AttachmentUnsupportedPreview");
        }
        catch (Exception ex) when (image && ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ShowStatus("Str_AttachmentInvalidImage");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void LoadImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            ShowStatus("Str_AttachmentInvalidImage");
            return;
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if ((long)info.Width * info.Height > MaxImagePixels)
        {
            ShowStatus("Str_AttachmentTooLargeToPreview");
            return;
        }

        var bitmap = new WriteableBitmap(new PixelSize(info.Width, info.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using (var pixels = bitmap.Lock())
            {
                if (codec.GetPixels(info, pixels.Address, pixels.RowBytes, new SKCodecOptions(0)) != SKCodecResult.Success)
                {
                    ShowStatus("Str_AttachmentInvalidImage");
                    return;
                }
            }
            _bitmap = bitmap;
        }
        finally { if (_bitmap != bitmap) bitmap.Dispose(); }
        ImagePreview.Source = _bitmap;
        ImagePanel.IsVisible = true;
    }

    private void ShowStatus(string key)
    {
        StatusText.Text = L(key);
        StatusText.IsVisible = true;
    }

    private void Edit_Click(object? sender, RoutedEventArgs e)
    {
        _editing = true;
        EditButton.IsVisible = CloseButton.IsVisible = false;
        SaveButton.IsVisible = CancelButton.IsVisible = true;
        TextPreview.IsReadOnly = false;
        TextPreview.IsUndoEnabled = true;
        UpdateSaveButton();
        TextPreview.Focus();
    }

    private void TextPreview_Changed(object? sender, TextChangedEventArgs e)
    {
        if (!_editing) return;
        EditErrorText.IsVisible = false;
        UpdateSaveButton();
    }

    private void UpdateSaveButton() =>
        SaveButton.IsEnabled = _document is not null && (TextPreview.Text ?? string.Empty) != _document.Text;

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!_editing || _document is not { } document || (TextPreview.Text ?? string.Empty) == document.Text)
            return;

        try
        {
            var bytes = document.Encode(TextPreview.Text ?? string.Empty);
            try { Close(new ProtectedBinary(_isProtected, bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (EncoderFallbackException) { EditErrorText.IsVisible = true; }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Attachment_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }
}
