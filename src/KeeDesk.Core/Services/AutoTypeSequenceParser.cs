using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public enum AutoTypeKey
{
    Tab, Enter, Space, Backspace, Delete, Escape, Up, Down, Left, Right, Home, End,
    PageUp, PageDown, Insert, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16
}

[Flags]
public enum AutoTypeModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Command = 8 }

public abstract record AutoTypeStep;
public sealed record AutoTypeTextStep(string Text) : AutoTypeStep;
public sealed record AutoTypeKeyStep(AutoTypeKey Key, int Repeat = 1) : AutoTypeStep
{
    public AutoTypeModifiers Modifiers { get; init; }
}
public sealed record AutoTypeCharacterKeyStep(char Character, AutoTypeModifiers Modifiers) : AutoTypeStep;
public sealed record AutoTypeDelayStep(int Milliseconds) : AutoTypeStep;
public sealed record AutoTypeSetDefaultDelayStep(int Milliseconds) : AutoTypeStep;
public sealed record AutoTypeClearFieldStep : AutoTypeStep;
public sealed record AutoTypeTotpStep(TotpSettings Settings) : AutoTypeStep;

public static partial class AutoTypeSequenceParser
{
    public const string DefaultSequence = "{USERNAME}{TAB}{PASSWORD}{ENTER}";

    [GeneratedRegex(@"^([A-Z][A-Z0-9]*)(?:(\s*=\s*|\s+)(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandRegex();

    public static string GetEffectiveSequence(KdbxEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.AutoType.DefaultSequence))
            return entry.AutoType.DefaultSequence;

        for (var group = entry.Parent; group is not null; group = group.Parent)
            if (!string.IsNullOrWhiteSpace(group.DefaultAutoTypeSequence))
                return group.DefaultAutoTypeSequence;
        return DefaultSequence;
    }

    public static List<AutoTypeStep> Parse(KdbxEntry entry, KdbxDatabase? db, string? overrideSequence = null) =>
        Tokenize(string.IsNullOrWhiteSpace(overrideSequence) ? GetEffectiveSequence(entry) : overrideSequence, entry, db);

    public static List<AutoTypeStep> Tokenize(string sequence) => Tokenize(sequence, null, null);

    private static List<AutoTypeStep> Tokenize(string sequence, KdbxEntry? entry, KdbxDatabase? vault)
    {
        var steps = new List<AutoTypeStep>();
        var text = new StringBuilder();
        var groups = new Stack<AutoTypeModifiers>();
        AutoTypeModifiers pending = default;

        void AddStep(AutoTypeStep step)
        {
            if (text.Length != 0)
            {
                steps.Add(new AutoTypeTextStep(text.ToString()));
                text.Clear();
            }
            steps.Add(step);
        }

        for (int index = 0; index < sequence.Length; index++)
        {
            var character = sequence[index];
            var modifier = character switch
            {
                '+' => AutoTypeModifiers.Shift,
                '^' => AutoTypeModifiers.Control,
                '%' => AutoTypeModifiers.Alt,
                _ => AutoTypeModifiers.None
            };
            if (modifier != AutoTypeModifiers.None)
            {
                pending |= modifier;
                continue;
            }

            groups.TryPeek(out var enclosing);
            var modifiers = pending | enclosing;
            if (character == '(' && pending != AutoTypeModifiers.None)
            {
                groups.Push(modifiers);
                pending = default;
                continue;
            }
            if (character == ')' && groups.Count != 0)
            {
                if (pending != default) throw new FormatException();
                groups.Pop();
                continue;
            }
            pending = default;

            if (character == '{')
            {
                int end = FindTokenEnd(sequence, index);
                var token = sequence[(index + 1)..end];
                index = end;

                if (token.Length == 1 && "+%^~()[]{}".Contains(token[0]))
                    character = token[0];
                else if (TryParseCommand(token, modifiers, out var command))
                {
                    AddStep(command);
                    continue;
                }
                else if (entry is not null && modifiers == default)
                {
                    if (token.StartsWith("PICKCHARS", StringComparison.OrdinalIgnoreCase) ||
                        token.StartsWith("PICKPASSWORDCHARS", StringComparison.OrdinalIgnoreCase))
                        AddStep(AutoTypePickCharactersStep.Create(token, entry));
                    else if (token.Equals("TOTP", StringComparison.OrdinalIgnoreCase) ||
                             token.Equals("TIMEOTP", StringComparison.OrdinalIgnoreCase))
                        AddStep(new AutoTypeTotpStep(TotpGenerator.TryExtractFromEntry(entry) ?? throw new FormatException()));
                    else
                        text.Append(ResolveField(token, entry, vault));
                    continue;
                }
                else if (entry is null && modifiers == default)
                {
                    text.Append('{').Append(token).Append('}');
                    continue;
                }
                else
                    throw new FormatException();
            }
            else if (character == '~')
            {
                AddStep(new AutoTypeKeyStep(AutoTypeKey.Enter) { Modifiers = modifiers });
                continue;
            }

            if (modifiers == default)
                text.Append(character);
            else
                AddStep(new AutoTypeCharacterKeyStep(character, modifiers));
        }
        if (pending != default || groups.Count > 0) throw new FormatException();
        if (text.Length > 0) steps.Add(new AutoTypeTextStep(text.ToString()));
        return steps;
    }

    private static int FindTokenEnd(string sequence, int start)
    {
        if (start + 2 < sequence.Length && sequence[start + 1] is '{' or '}' && sequence[start + 2] == '}')
            return start + 2;

        int depth = 1;
        for (int i = start + 1; i < sequence.Length; i++)
        {
            if (sequence[i] == '{') depth++;
            if (sequence[i] == '}' && --depth == 0) return i;
        }
        throw new FormatException();
    }

    private static bool TryParseCommand(string token, AutoTypeModifiers modifiers, out AutoTypeStep step)
    {
        step = null!;
        var match = CommandRegex().Match(token);
        if (!match.Success) return false;

        var name = match.Groups[1].Value.ToUpperInvariant();
        bool hasArgument = match.Groups[3].Success;
        int value = hasArgument ? int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture) : 1;
        if (name == "DELAY" && modifiers == default)
        {
            int milliseconds = hasArgument ? Math.Clamp(value, 0, 60_000) : 100;
            step = match.Groups[2].Value.Trim() == "="
                ? new AutoTypeSetDefaultDelayStep(milliseconds) : new AutoTypeDelayStep(milliseconds);
            return true;
        }
        if (name == "CLEARFIELD" && modifiers == default && !hasArgument)
        {
            step = new AutoTypeClearFieldStep();
            return true;
        }

        name = name switch
        {
            "RETURN" => "ENTER", "ESC" => "ESCAPE", "DEL" => "DELETE",
            "BS" or "BKSP" => "BACKSPACE", "PGUP" => "PAGEUP", "PGDN" => "PAGEDOWN", "INS" => "INSERT",
            _ => name
        };
        if (!Enum.TryParse<AutoTypeKey>(name, true, out var key))
            return false;

        step = new AutoTypeKeyStep(key, Math.Clamp(value, 1, 100)) { Modifiers = modifiers };
        return true;
    }

    private static string ResolveField(string token, KdbxEntry entry, KdbxDatabase? vault)
    {
        // Compile only the sequence. Characters from entry fields never become keystroke commands.
        if (token.StartsWith("S:", StringComparison.OrdinalIgnoreCase))
            return ResolveFieldReferences(entry.GetString(token[2..]), entry, vault);
        if (token.StartsWith("REF:", StringComparison.OrdinalIgnoreCase))
        {
            var expression = "{" + token + "}";
            var value = FieldReferenceResolver.Resolve(expression, entry, vault);
            return value != expression ? value : throw new FormatException();
        }
        if (token.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (token.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
            return ResolveUrlPart(token[4..], ResolveFieldReferences(entry.Url, entry, vault));
        return token.ToUpperInvariant() switch
        {
            "TITLE" => ResolveFieldReferences(entry.Title, entry, vault),
            "USERNAME" => ResolveFieldReferences(entry.UserName, entry, vault),
            "PASSWORD" => ResolveFieldReferences(entry.Password, entry, vault),
            "URL" => ResolveFieldReferences(entry.Url, entry, vault),
            "NOTES" => ResolveFieldReferences(entry.Notes, entry, vault),
            "UUID" => entry.Uuid.ToString("N").ToUpperInvariant(),
            "GROUP" => entry.Parent?.Name ?? string.Empty,
            "GROUP_PATH" => GroupPath(entry.Parent),
            "DIRSEP" => Path.DirectorySeparatorChar.ToString(),
            "APPDIR" => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            "DB_PATH" => vault?.FilePath ?? string.Empty,
            "DB_DIR" => Path.GetDirectoryName(vault?.FilePath) ?? string.Empty,
            "DB_NAME" => Path.GetFileName(vault?.FilePath) ?? string.Empty,
            "DB_BASENAME" => Path.GetFileNameWithoutExtension(vault?.FilePath) ?? string.Empty,
            "DB_EXT" => Path.GetExtension(vault?.FilePath)?.TrimStart('.') ?? string.Empty,
            _ => throw new FormatException()
        };
    }

    private static string ResolveFieldReferences(string value, KdbxEntry entry, KdbxDatabase? vault) =>
        value.StartsWith("{REF:", StringComparison.OrdinalIgnoreCase) && value.EndsWith('}') &&
        value.IndexOf('}') == value.Length - 1
            ? FieldReferenceResolver.Resolve(value, entry, vault)
            : value;

    private static string ResolveUrlPart(string part, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        return part.ToUpperInvariant() switch
        {
            "SCM" => uri.Scheme,
            "RMVSCM" => url[(uri.Scheme.Length + (url.AsSpan(uri.Scheme.Length).StartsWith("://") ? 3 : 1))..],
            "HOST" => uri.Host,
            "PORT" => uri.Port < 0 ? string.Empty : uri.Port.ToString(CultureInfo.InvariantCulture),
            "PATH" => uri.AbsolutePath,
            "QUERY" => uri.Query,
            "USERINFO" => uri.UserInfo,
            "USERNAME" => uri.UserInfo.Split(':', 2)[0],
            "PASSWORD" => uri.UserInfo.Split(':', 2).ElementAtOrDefault(1) ?? string.Empty,
            _ => throw new FormatException()
        };
    }

    private static string GroupPath(KdbxGroup? group)
    {
        var path = new Stack<string>();
        for (; group is not null; group = group.Parent)
            path.Push(group.Name);
        return string.Join("/", path);
    }
}
