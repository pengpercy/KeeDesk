using System.Globalization;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record AutoTypePickCharactersStep(string Field, string Value, string Id, int? Count, bool Hide,
    bool ConvertToArrows, string? ComboBoxFormat, int Offset) : AutoTypeStep
{
    public string[] GetCharacters()
    {
        var elements = StringInfo.GetTextElementEnumerator(Value);
        var characters = new List<string>();
        while (elements.MoveNext()) characters.Add(elements.GetTextElement());
        return characters.ToArray();
    }

    public IEnumerable<AutoTypeStep> CreateSteps(IReadOnlyList<int> positions)
    {
        var characters = GetCharacters();
        if (positions.Count == 0 || (Count is { } count && count != positions.Count) ||
            positions.Any(position => position < 1 || position > characters.Length))
            throw new FormatException();

        if (!ConvertToArrows)
        {
            yield return new AutoTypeTextStep(string.Concat(positions.Select(position => characters[position - 1])));
            yield break;
        }

        foreach (int position in positions)
        {
            string character = characters[position - 1];
            int repeats = character.Length == 1 ? GetComboBoxIndex(character[0]) : -1;
            if (repeats < 0 || repeats + Offset is < 0 or > 1000) throw new FormatException();
            if (repeats + Offset > 0)
                yield return new AutoTypeKeyStep(AutoTypeKey.Down, repeats + Offset);
        }
    }

    private int GetComboBoxIndex(char character)
    {
        if (ComboBoxFormat is null)
        {
            if (char.IsAsciiDigit(character)) return character - '0';
            return char.IsAsciiLetter(character) ? char.ToUpperInvariant(character) - 'A' : -1;
        }

        var values = string.Concat(ComboBoxFormat.Select(item => item switch
        {
            '0' => "0123456789",
            '1' => "1234567890",
            'A' => "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            'a' => "abcdefghijklmnopqrstuvwxyz",
            _ => "\0"
        }));
        if (character == '\0') return -1;
        var comparison = ComboBoxFormat.Contains('A') && ComboBoxFormat.Contains('a')
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return values.IndexOf(character.ToString(), comparison);
    }

    internal static AutoTypePickCharactersStep Create(string token, KdbxEntry entry)
    {
        var parts = token.Split(':', 3, StringSplitOptions.TrimEntries);
        if (!parts[0].Equals("PICKCHARS", StringComparison.OrdinalIgnoreCase) &&
            !parts[0].Equals("PICKPASSWORDCHARS", StringComparison.OrdinalIgnoreCase))
            throw new FormatException();

        var field = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : KdbxConstants.FieldPassword;
        field = entry.Strings.Keys.FirstOrDefault(key => string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                ?? field;
        var value = entry.GetString(field);
        if (value.Length == 0) throw new FormatException();

        string id = token;
        bool hide = true;
        int? count = null;
        bool convert = false;
        string? format = null;
        int offset = 0;
        if (parts.Length == 3)
        {
            foreach (var option in parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = option.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || pair[1].Length == 0) throw new FormatException();
                switch (pair[0].ToUpperInvariant())
                {
                    case "ID":
                        id = "ID:" + pair[1];
                        break;
                    case "C":
                    case "COUNT":
                        count = int.Parse(pair[1], CultureInfo.InvariantCulture);
                        if (count is < 1 or > 1024) throw new FormatException();
                        break;
                    case "HIDE":
                        hide = bool.Parse(pair[1]);
                        break;
                    case "CONV" when pair[1].Equals("D", StringComparison.OrdinalIgnoreCase):
                        convert = true;
                        break;
                    case "CONV-FMT" when pair[1].All(item => item is '0' or '1' or 'A' or 'a' or '?'):
                        format = pair[1];
                        break;
                    case "CONV-OFFSET":
                        offset = int.Parse(pair[1], CultureInfo.InvariantCulture);
                        if (offset is < -1000 or > 1000) throw new FormatException();
                        break;
                    default:
                        throw new FormatException();
                }
            }
        }

        return new AutoTypePickCharactersStep(field, value, id, count, hide, convert, format, offset);
    }
}
