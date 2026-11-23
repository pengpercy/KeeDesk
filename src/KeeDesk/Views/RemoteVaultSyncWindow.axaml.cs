using Avalonia.Controls;
using Avalonia.Interactivity;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Views;

public partial class RemoteVaultSyncWindow : Window
{
    private CancellationTokenSource? _cancellation;
    private bool _running;

    public RemoteVaultSyncWindow() => InitializeComponent();

    internal RemoteVaultSyncWindow(Func<CancellationToken, Task<KdbxDatabase?>> synchronize,
        CancellationTokenSource cancellation) : this()
    {
        _cancellation = cancellation;
        _running = true;
        Opened += async (_, _) =>
        {
            KdbxDatabase? merged = null;
            Exception? error = null;
            try { merged = await synchronize(cancellation.Token); }
            catch (Exception ex) { error = ex; }
            finally
            {
                _running = false;
                Close((merged, error));
            }
        };
        Closing += (_, args) =>
        {
            if (!_running) return;
            args.Cancel = true;
            RequestCancellation();
        };
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => RequestCancellation();

    private void RequestCancellation()
    {
        CancelButton.IsEnabled = false;
        _cancellation?.Cancel();
    }
}
