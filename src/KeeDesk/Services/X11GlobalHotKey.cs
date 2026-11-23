using System.Runtime.InteropServices;
using KeeDesk.Core.Services;

namespace KeeDesk.Services;

internal static class X11GlobalHotKey
{
    private const string Xcb = "libxcb.so.1";
    private const uint NumLock = 0xFF7F;
    private const ushort CapsLockMask = 2;

    internal static Action? Start(DesktopShortcut shortcut, Action pressed, CancellationToken token)
    {
        var connection = xcb_connect(0, out int screenNumber);
        if (connection == 0) return null;
        bool registered = false;
        try
        {
            if (xcb_connection_has_error(connection) != 0) return null;
            var setup = xcb_get_setup(connection);
            if (setup == 0) return null;
            var screen = xcb_setup_roots_iterator(setup);
            for (int i = 0; i < screenNumber && screen.Remaining > 0; i++)
                xcb_screen_next(ref screen);
            if (screen.Remaining == 0) return null;
            uint root = unchecked((uint)Marshal.ReadInt32(screen.Data));
            byte minimum = Marshal.ReadByte(setup, 34);
            byte count = (byte)(Marshal.ReadByte(setup, 35) - minimum + 1);
            if (count == 0) return null;
            var (key, numLock) = ReadKeyCodes(connection, minimum, count, char.ToLowerInvariant(shortcut.Key));
            if (key == 0) return null;

            ushort modifiers = 0;
            if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Shift)) modifiers |= 1;
            if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Control)) modifiers |= 4;
            if (shortcut.Modifiers.HasFlag(AutoTypeModifiers.Alt)) modifiers |= 8;
            ushort numLockMask = ReadModifierMask(connection, numLock);
            var masks = new ushort[] { 0, CapsLockMask, numLockMask, (ushort)(CapsLockMask | numLockMask) };
            foreach (var mask in masks.Distinct())
            {
                const byte asynchronous = 1;
                uint request = xcb_grab_key_checked(connection, 0, root, (ushort)(modifiers | mask), key, asynchronous, asynchronous);
                nint error = xcb_request_check(connection, request);
                if (error == 0) continue;
                free(error);
                return null;
            }

            registered = true;
            return () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && xcb_connection_has_error(connection) == 0)
                    {
                        var @event = xcb_poll_for_event(connection);
                        if (@event == 0)
                        {
                            token.WaitHandle.WaitOne(20);
                            continue;
                        }
                        bool keyPressed = (Marshal.ReadByte(@event) & 0x7F) == 2;
                        free(@event);
                        if (keyPressed && !token.IsCancellationRequested) pressed();
                    }
                }
                finally { xcb_disconnect(connection); }
            };
        }
        finally
        {
            if (!registered) xcb_disconnect(connection);
        }
    }

    private static (byte Key, byte NumLock) ReadKeyCodes(nint connection, byte minimum, byte count, uint symbol)
    {
        uint request = xcb_get_keyboard_mapping(connection, minimum, count);
        nint reply = xcb_get_keyboard_mapping_reply(connection, request, 0);
        if (reply == 0) return default;
        try
        {
            byte columns = Marshal.ReadByte(reply, 1);
            nint symbols = xcb_get_keyboard_mapping_keysyms(reply);
            byte key = 0, numLock = 0;
            for (int i = 0; i < count; i++)
            {
                // Check the two standard levels for the shortcut and all levels for Num Lock.
                for (int col = 0; col < columns; col++)
                {
                    var value = unchecked((uint)Marshal.ReadInt32(symbols, (i * columns + col) * sizeof(uint)));
                    if (col < 2 && key == 0 && value == symbol) key = (byte)(minimum + i);
                    if (value == NumLock) numLock = (byte)(minimum + i);
                }
            }
            return (key, numLock);
        }
        finally { free(reply); }
    }

    private static ushort ReadModifierMask(nint connection, byte key)
    {
        if (key == 0) return 0;
        uint request = xcb_get_modifier_mapping(connection);
        nint reply = xcb_get_modifier_mapping_reply(connection, request, 0);
        if (reply == 0) return 0;
        try
        {
            byte columns = Marshal.ReadByte(reply, 1);
            nint keys = xcb_get_modifier_mapping_keycodes(reply);
            for (int i = 0; i < 8 * columns; i++)
                if (Marshal.ReadByte(keys, i) == key)
                    return (ushort)(1 << (i / columns));
            return 0;
        }
        finally { free(reply); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator { public nint Data; public int Remaining; public int Index; }
    [DllImport(Xcb)] private static extern nint xcb_connect(nint display, out int screenNumber);
    [DllImport(Xcb)] private static extern int xcb_connection_has_error(nint connection);
    [DllImport(Xcb)] private static extern void xcb_disconnect(nint connection);
    [DllImport(Xcb)] private static extern nint xcb_get_setup(nint connection);
    [DllImport(Xcb)] private static extern ScreenIterator xcb_setup_roots_iterator(nint setup);
    [DllImport(Xcb)] private static extern void xcb_screen_next(ref ScreenIterator screen);
    [DllImport(Xcb)] private static extern uint xcb_get_keyboard_mapping(nint connection, byte first, byte count);
    [DllImport(Xcb)] private static extern nint xcb_get_keyboard_mapping_reply(nint connection, uint cookie, nint error);
    [DllImport(Xcb)] private static extern nint xcb_get_keyboard_mapping_keysyms(nint reply);
    [DllImport(Xcb)] private static extern uint xcb_get_modifier_mapping(nint connection);
    [DllImport(Xcb)] private static extern nint xcb_get_modifier_mapping_reply(nint connection, uint cookie, nint error);
    [DllImport(Xcb)] private static extern nint xcb_get_modifier_mapping_keycodes(nint reply);
    [DllImport(Xcb)] private static extern uint xcb_grab_key_checked(nint connection, byte ownerEvents, uint window,
        ushort modifiers, byte key, byte pointerMode, byte keyboardMode);
    [DllImport(Xcb)] private static extern nint xcb_request_check(nint connection, uint cookie);
    [DllImport(Xcb)] private static extern nint xcb_poll_for_event(nint connection);
    [DllImport("libc.so.6")] private static extern void free(nint pointer);
}
