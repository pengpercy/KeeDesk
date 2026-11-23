using System.Runtime.InteropServices;
using KeeDesk.Core.Services;

namespace KeeDesk.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    public event Action? HotKeyPressed;
    public DesktopShortcut? Shortcut { get; private set; }

    private nint _macHotKeyRef;
    private nint _macHandlerRef;
    private EventHandlerDelegate? _macCallback;
    private IDisposable? _keyboardLoop;
    private static int _nextMacId;

    public bool TryRegister(DesktopShortcut shortcut)
    {
        if (Shortcut == shortcut) return true;
        if (Shortcut is not null || !shortcut.IsValid) return false;
        try
        {
            bool registered = OperatingSystem.IsMacOS() ? TryRegisterMacHotKey(shortcut) :
                OperatingSystem.IsWindows() ? TryRunKeyboardLoop(token => RunWindows(shortcut, token)) :
                AutoTypeTarget.IsX11 && TryRunKeyboardLoop(token =>
                    X11GlobalHotKey.Start(shortcut, () => HotKeyPressed?.Invoke(), token));
            if (registered) Shortcut = shortcut;
            return registered;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Shortcut = null;
        if (_macHotKeyRef != 0)
        {
            _ = UnregisterEventHotKey(_macHotKeyRef);
            _macHotKeyRef = 0;
        }
        if (_macHandlerRef != 0)
        {
            _ = RemoveEventHandler(_macHandlerRef);
            _macHandlerRef = 0;
            _macCallback = null;
        }
        _keyboardLoop?.Dispose();
        _keyboardLoop = null;
    }

    private bool TryRegisterMacHotKey(DesktopShortcut shortcut)
    {
        const uint keyboard = 0x6B657962; // 'keyb'
        const uint hotKeyPressed = 6;
        const uint directObject = 0x2D2D2D2D; // '----'
        const uint hotKeyId = 0x686B6964; // 'hkid'
        var target = GetApplicationEventTarget();
        if (target == 0) return false;

        var id = new EventHotKeyId(0x4B454544, unchecked((uint)Interlocked.Increment(ref _nextMacId)));
        _macCallback = (_, @event, _) =>
        {
            if (GetEventParameter(@event, directObject, hotKeyId, 0, (uint)Marshal.SizeOf<EventHotKeyId>(), 0, out var actual) != 0 ||
                actual.Signature != id.Signature || actual.Id != id.Id)
                return -9874; // eventNotHandledErr

            HotKeyPressed?.Invoke();
            return 0;
        };

        var spec = new EventTypeSpec(keyboard, hotKeyPressed);
        if (InstallEventHandler(target, _macCallback, 1, in spec, 0, out _macHandlerRef) != 0)
            return false;

        uint flags = 0;
        if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Command)) flags |= 0x0100;
        if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Shift)) flags |= 0x0200;
        if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Alt)) flags |= 0x0800;
        if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Control)) flags |= 0x1000;
        if (RegisterEventHotKey(DesktopShortcut.GetMacKeyCode(char.ToUpperInvariant(shortcut.Key)), flags,
            id, target, 0, out _macHotKeyRef) == 0)
            return true;

        Dispose();
        return false;
    }

    private Action? RunWindows(DesktopShortcut shortcut, CancellationToken token)
    {
        const uint wmHotKey = 0x0312;
        uint flags = 0x4000;
        if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Alt)) flags |= 0x0001;
        if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Control)) flags |= 0x0002;
        if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Shift)) flags |= 0x0004;
        if (!RegisterHotKey(0, 1, flags, DesktopShortcut.GetKeyCode(shortcut.Key)))
            return null;
        return () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (PeekMessageW(out _, 0, wmHotKey, wmHotKey, 1))
                        HotKeyPressed?.Invoke();
                    token.WaitHandle.WaitOne(20);
                }
            }
            finally
            {
                _ = UnregisterHotKey(0, 1);
            }
        };
    }

    private bool TryRunKeyboardLoop(Func<CancellationToken, Action?> start)
    {
        var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var loop = start(cancellation.Token);
                started.TrySetResult(loop is not null);
                loop?.Invoke();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
            {
                started.TrySetResult(false);
            }
        }) { IsBackground = true, Name = "KeeDesk global hotkey" };
        thread.Start();
        var registration = new KeyboardLoop(thread, cancellation);
        if (started.Task.Wait(TimeSpan.FromSeconds(2)) && started.Task.Result)
        {
            _keyboardLoop = registration;
            return true;
        }
        registration.Dispose();
        return false;
    }

    private sealed class KeyboardLoop(Thread thread, CancellationTokenSource cancellation) : IDisposable
    {
        public void Dispose()
        {
            cancellation.Cancel();
            if (thread.Join(200))
                cancellation.Dispose();
            else
                _ = Task.Run(() => { thread.Join(); cancellation.Dispose(); });
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventTypeSpec(uint EventClass, uint EventKind);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventHotKeyId(uint Signature, uint Id);
    [StructLayout(LayoutKind.Sequential)]
    private struct WinMsg
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerDelegate(nint nextHandler, nint @event, nint userData);
    [DllImport(Carbon)] private static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(nint target, EventHandlerDelegate handler, uint numTypes,
        in EventTypeSpec spec, nint userData, out nint reference);
    [DllImport(Carbon)] private static extern int GetEventParameter(nint @event, uint name, uint desiredType, nint actualType,
        uint size, nint actualSize, out EventHotKeyId result);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(nint handler);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint key, uint flags, EventHotKeyId id,
        nint target, uint options, out nint reference);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(nint key);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint flags, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out WinMsg message, nint window, uint min, uint max, uint remove);
}
