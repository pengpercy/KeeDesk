using Avalonia.Controls;
using Avalonia.Interactivity;
using KeeDesk.Core.Services;

namespace KeeDesk.Views;

public partial class RemoteVaultConnectionWindow : Window
{
    public RemoteVaultConnectionWindow() : this(null) { }

    public RemoteVaultConnectionWindow(Uri? endpoint)
    {
        InitializeComponent();
        UrlBox.Text = endpoint?.AbsoluteUri;
        Opened += (_, _) => UrlBox.Focus();
        Closed += (_, _) => ServerPasswordBox.Text = string.Empty;
    }

    private void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(UrlBox.Text?.Trim(), UriKind.Absolute, out var endpoint) ||
            !RemoteVaultClient.IsSupportedEndpoint(endpoint) || UsernameBox.Text?.Contains(':') == true)
        {
            ErrorText.IsVisible = true;
            return;
        }

        Close(new RemoteVaultClient(endpoint, UsernameBox.Text, ServerPasswordBox.Text));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
