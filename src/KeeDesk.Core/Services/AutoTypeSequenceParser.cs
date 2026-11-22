using System.Text;
using System.Text.RegularExpressions;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public enum AutoTypeKey
{
    Tab,
    Enter,
    Space,
    Backspace,
    Delete,
    Escape,
    Up,
    Down,
    Left,
    Right,
    Home,
    End
}

public abstract record AutoTypeStep;
public sealed record AutoTypeTextStep(string Text) : AutoTypeStep;
public sealed record AutoTypeKeyStep(AutoTypeKey Key, int Repeat = 1) : AutoTypeStep;
public sealed record AutoTypeDelayStep(int Milliseconds) : AutoTypeStep;
public sealed record AutoTypeSetDefaultDelayStep(int Milliseconds) : AutoTypeStep;

/// <summary>
/// 对标原版 KeePass 2.x 的自动输入序列解析器：
/// 先解析字段与引用占位符，再拆分为强类型文本/功能键/延迟动作列表。
/// </summary>
public static partial class AutoTypeSequenceParser
{
    public const string DefaultSequence = "{USERNAME}{TAB}{PASSWORD}{ENTER}";

    [GeneratedRegex(@"\{([A-Z]+)(?:(\s*=\s*|\s+)(\d+))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    /// <summary>
    /// 获取条目生效的自动输入序列（条目自定义 -> 父分组继承 -> 默认序列）。
    /// </summary>
    public static string GetEffectiveSequence(KdbxEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.AutoType.DefaultSequence))
            return entry.AutoType.DefaultSequence;

        var g = entry.Parent;
        while (g is not null)
        {
            if (!string.IsNullOrWhiteSpace(g.DefaultAutoTypeSequence))
                return g.DefaultAutoTypeSequence;
            g = g.Parent;
        }

        return DefaultSequence;
    }

    public static List<AutoTypeStep> Parse(KdbxEntry entry, KdbxDatabase? db, string? overrideSequence = null)
    {
        var rawSeq = string.IsNullOrWhiteSpace(overrideSequence) ? GetEffectiveSequence(entry) : overrideSequence;
        var resolved = FieldReferenceResolver.Resolve(rawSeq, entry, db);
        return Tokenize(resolved);
    }

    public static List<AutoTypeStep> Tokenize(string sequence)
    {
        var steps = new List<AutoTypeStep>();
        if (string.IsNullOrEmpty(sequence)) return steps;

        int pos = 0;
        var textBuf = new StringBuilder();

        void FlushText()
        {
            if (textBuf.Length > 0)
            {
                steps.Add(new AutoTypeTextStep(textBuf.ToString()));
                textBuf.Clear();
            }
        }

        foreach (Match m in TokenRegex().Matches(sequence))
        {
            if (m.Index > pos)
                textBuf.Append(sequence, pos, m.Index - pos);

            var cmd = m.Groups[1].Value.ToUpperInvariant();
            var sep = m.Groups[2].Value.Trim();
            int arg = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var n) ? n : 1;

            if (cmd == "DELAY")
            {
                FlushText();
                int ms = Math.Clamp(m.Groups[3].Success ? arg : 100, 0, 10_000);
                if (sep == "=")
                    steps.Add(new AutoTypeSetDefaultDelayStep(ms));
                else
                    steps.Add(new AutoTypeDelayStep(ms));
            }
            else if (TryMapKey(cmd, out var key))
            {
                FlushText();
                steps.Add(new AutoTypeKeyStep(key, Math.Clamp(arg, 1, 50)));
            }
            else
            {
                // 未识别的花括号文本原样保留为普通文本
                textBuf.Append(m.Value);
            }

            pos = m.Index + m.Length;
        }

        if (pos < sequence.Length)
            textBuf.Append(sequence, pos, sequence.Length - pos);

        FlushText();
        return steps;
    }

    private static bool TryMapKey(string cmd, out AutoTypeKey key)
    {
        switch (cmd)
        {
            case "TAB": key = AutoTypeKey.Tab; return true;
            case "ENTER": case "RETURN": key = AutoTypeKey.Enter; return true;
            case "SPACE": key = AutoTypeKey.Space; return true;
            case "BACKSPACE": case "BS": case "BKSP": key = AutoTypeKey.Backspace; return true;
            case "DELETE": case "DEL": key = AutoTypeKey.Delete; return true;
            case "ESC": case "ESCAPE": key = AutoTypeKey.Escape; return true;
            case "UP": key = AutoTypeKey.Up; return true;
            case "DOWN": key = AutoTypeKey.Down; return true;
            case "LEFT": key = AutoTypeKey.Left; return true;
            case "RIGHT": key = AutoTypeKey.Right; return true;
            case "HOME": key = AutoTypeKey.Home; return true;
            case "END": key = AutoTypeKey.End; return true;
            default: key = default; return false;
        }
    }
}
