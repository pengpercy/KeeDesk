using KeeDesk.Core.Services;

namespace KeeDesk.Services;

public sealed record DesktopShortcut(char Key, AutoTypeModifiers Modifiers)
{
    public static DesktopShortcut Default { get; } = new('A', AutoTypeModifiers.Control | AutoTypeModifiers.Alt);

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || AutoTypeTarget.IsX11;

    internal bool IsValid => (char.IsAsciiLetterOrDigit(Key) &&
        (Modifiers & (AutoTypeModifiers.Control | AutoTypeModifiers.Alt | AutoTypeModifiers.Command)) != 0 &&
        (OperatingSystem.IsMacOS() || !Modifiers.HasFlag(AutoTypeModifiers.Command)));

    public IEnumerable<string> GetLabels()
    {
        bool mac = OperatingSystem.IsMacOS();
        if (Modifiers.HasFlag(AutoTypeModifiers.Control)) yield return mac ? "⌃" : "Ctrl";
        if (Modifiers.HasFlag(AutoTypeModifiers.Alt)) yield return mac ? "⌥" : "Alt";
        if (Modifiers.HasFlag(AutoTypeModifiers.Shift)) yield return mac ? "⇧" : "Shift";
        if (Modifiers.HasFlag(AutoTypeModifiers.Command)) yield return mac ? "⌘" : "Super";
        yield return char.ToUpperInvariant(Key).ToString();
    }

    internal static ushort GetKeyCode(char key) => char.IsAsciiLetterOrDigit(key)
        ? char.ToUpperInvariant(key)
        : throw new NotSupportedException();

    internal static ushort GetMacKeyCode(char key) => key switch
    {
        'A' => 0, 'S' => 1, 'D' => 2, 'F' => 3, 'H' => 4, 'G' => 5, 'Z' => 6, 'X' => 7, 'C' => 8,
        'V' => 9, 'B' => 11, 'Q' => 12, 'W' => 13, 'E' => 14, 'R' => 15, 'Y' => 16, 'T' => 17,
        '1' => 18, '2' => 19, '3' => 20, '4' => 21, '6' => 22, '5' => 23, '9' => 25, '7' => 26,
        '8' => 28, '0' => 29, 'O' => 31, 'U' => 32, 'I' => 34, 'P' => 35, 'L' => 37, 'J' => 38,
        'K' => 40, 'N' => 45, 'M' => 46,
        _ => throw new NotSupportedException()
    };
}
