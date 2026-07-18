using System.Globalization;
using System.Text;

namespace WoadRaiders.Core;

/// <summary>
/// A minimal, engine-free reader for Godot's TEXT scene format (.tscn,
/// format 3): sections ([gd_scene], [ext_resource], [sub_resource], [node])
/// with their header attributes and body properties. It parses just enough of
/// Godot's value grammar for map work — numbers, strings, bools, constructor
/// calls like Vector3(..)/Transform3D(..)/PackedFloat32Array(..), resource
/// references, and lists — and carries anything else as unparsed raw text, so
/// a hand-made scene full of materials and particles never trips it.
///
/// This is what lets the whole map pipeline live in plain C#: the server loads
/// realm .tscn files directly, the generator round-trips its own output, and
/// all of it is unit-testable without an engine.
/// </summary>
public sealed class TscnDocument
{
    /// <summary>One [kind key=value ...] section and its property lines.</summary>
    public sealed class Section
    {
        public string Kind = "";
        public Dictionary<string, TscnValue> Attributes { get; } = new();
        public Dictionary<string, TscnValue> Properties { get; } = new();

        public string? AttributeString(string key) =>
            Attributes.TryGetValue(key, out var v) ? v.AsString : null;
    }

    private readonly List<Section> _sections = new();

    public IReadOnlyList<Section> Sections => _sections;

    /// <summary>The [node ...] sections, in tree (file) order.</summary>
    public IEnumerable<Section> Nodes => _sections.Where(s => s.Kind == "node");

    /// <summary>A [sub_resource] by its id attribute, or null.</summary>
    public Section? SubResource(string id) =>
        _sections.FirstOrDefault(s => s.Kind == "sub_resource" && s.AttributeString("id") == id);

    /// <summary>An [ext_resource] by its id attribute, or null.</summary>
    public Section? ExtResource(string id) =>
        _sections.FirstOrDefault(s => s.Kind == "ext_resource" && s.AttributeString("id") == id);

    public static TscnDocument Parse(string text)
    {
        var doc = new TscnDocument();
        Section? current = null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(';') || trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (line.StartsWith('['))
            {
                current = ParseHeader(line.Trim());
                doc._sections.Add(current);
                i++;
                continue;
            }

            // A property line: "name = value". Values may span lines (nested
            // brackets in dictionaries/arrays) — accumulate until balanced.
            var eq = FindTopLevelEquals(line);
            if (current is null || eq < 0)
            {
                i++; // stray line (or pre-header junk) — tolerate and move on
                continue;
            }

            var name = line[..eq].Trim();
            var valueText = new StringBuilder(line[(eq + 1)..]);
            while (!IsBalanced(valueText.ToString()) && i + 1 < lines.Length)
            {
                i++;
                valueText.Append('\n').Append(lines[i]);
            }
            current.Properties[name] = TscnValue.Parse(valueText.ToString().Trim());
            i++;
        }

        return doc;
    }

    private static Section ParseHeader(string line)
    {
        // [kind key=value key=value ...]
        var inner = line[1..^1].Trim();
        var section = new Section();

        var cursor = 0;
        SkipSpace(inner, ref cursor);
        var kindStart = cursor;
        while (cursor < inner.Length && !char.IsWhiteSpace(inner[cursor]))
            cursor++;
        section.Kind = inner[kindStart..cursor];

        while (cursor < inner.Length)
        {
            SkipSpace(inner, ref cursor);
            if (cursor >= inner.Length)
                break;
            var keyStart = cursor;
            while (cursor < inner.Length && inner[cursor] != '=')
                cursor++;
            var key = inner[keyStart..cursor].Trim();
            if (cursor >= inner.Length)
                break;
            cursor++; // '='
            var valueText = ReadBalancedToken(inner, ref cursor);
            if (key.Length > 0)
                section.Attributes[key] = TscnValue.Parse(valueText);
        }
        return section;
    }

    private static void SkipSpace(string s, ref int cursor)
    {
        while (cursor < s.Length && char.IsWhiteSpace(s[cursor]))
            cursor++;
    }

    /// <summary>Read one attribute value: to the next top-level whitespace.</summary>
    private static string ReadBalancedToken(string s, ref int cursor)
    {
        var start = cursor;
        var depth = 0;
        var inString = false;
        while (cursor < s.Length)
        {
            var c = s[cursor];
            if (inString)
            {
                if (c == '\\')
                    cursor++; // skip the escaped char
                else if (c == '"')
                    inString = false;
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (char.IsWhiteSpace(c) && depth == 0)
            {
                break;
            }
            cursor++;
        }
        return s[start..cursor];
    }

    /// <summary>The index of the property '=' — outside any string or bracket.</summary>
    private static int FindTopLevelEquals(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '=')
            {
                return i;
            }
            else if (c is '(' or '[' or '{')
            {
                return -1; // a bracket before '=' — not a property line
            }
        }
        return -1;
    }

    /// <summary>Are all brackets closed and strings terminated?</summary>
    private static bool IsBalanced(string s)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
            }
            else if (c == '"')
                inString = true;
            else if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
        }
        return depth <= 0 && !inString;
    }
}

/// <summary>
/// One parsed Godot text value: a number, string, bool, list, a constructor
/// call (Vector3(..), Transform3D(..), SubResource(..), PackedFloat32Array(..)
/// — name + arguments), or raw unparsed text for anything exotic.
/// </summary>
public readonly struct TscnValue
{
    public enum ValueKind { Number, Text, Bool, Call, List, Raw }

    public ValueKind Kind { get; }
    private readonly double _number;
    private readonly string? _text;      // Text and Raw payload, and Call name
    private readonly TscnValue[]? _items; // Call args and List items

    private TscnValue(ValueKind kind, double number = 0, string? text = null, TscnValue[]? items = null)
    {
        Kind = kind;
        _number = number;
        _text = text;
        _items = items;
    }

    public double AsNumber => Kind == ValueKind.Number ? _number : throw Wrong("a number");
    public string AsString => Kind == ValueKind.Text ? _text! : throw Wrong("a string");
    public bool AsBool => Kind == ValueKind.Bool ? _number != 0 : throw Wrong("a bool");
    public string CallName => Kind == ValueKind.Call ? _text! : throw Wrong("a call");
    public IReadOnlyList<TscnValue> Items =>
        Kind is ValueKind.Call or ValueKind.List ? _items! : throw Wrong("a call or list");

    public float AsFloat => (float)AsNumber;
    public int AsInt => (int)AsNumber;

    /// <summary>Every argument as a float — for Vector3/Transform3D/packed arrays.</summary>
    public float[] Floats() => Items.Select(v => v.AsFloat).ToArray();

    private InvalidDataException Wrong(string wanted) => new($"expected {wanted}, found {Kind}");

    public static TscnValue Parse(string text)
    {
        var cursor = 0;
        var value = ParseOne(text, ref cursor);
        return value;
    }

    private static TscnValue ParseOne(string s, ref int cursor)
    {
        while (cursor < s.Length && char.IsWhiteSpace(s[cursor]))
            cursor++;
        if (cursor >= s.Length)
            return new TscnValue(ValueKind.Raw, text: "");

        var c = s[cursor];
        if (c == '&') // StringName prefix — treat as its string
            cursor++;

        if (cursor < s.Length && s[cursor] == '"')
            return new TscnValue(ValueKind.Text, text: ParseString(s, ref cursor));

        if (c == '[')
        {
            cursor++; // '['
            var items = ParseArguments(s, ref cursor, ']');
            return new TscnValue(ValueKind.List, items: items);
        }

        if (c == '{')
        {
            // Dictionaries aren't needed for map work — consume balanced, keep raw.
            var start = cursor;
            ConsumeBalanced(s, ref cursor);
            return new TscnValue(ValueKind.Raw, text: s[start..cursor]);
        }

        if (c == '-' || c == '+' || char.IsDigit(c))
        {
            var start = cursor;
            while (cursor < s.Length && (char.IsDigit(s[cursor]) || s[cursor] is '.' or '-' or '+' or 'e' or 'E'))
                cursor++;
            return new TscnValue(ValueKind.Number,
                double.Parse(s[start..cursor], CultureInfo.InvariantCulture));
        }

        if (char.IsLetter(c) || c == '_')
        {
            var start = cursor;
            while (cursor < s.Length && (char.IsLetterOrDigit(s[cursor]) || s[cursor] is '_' or '.'))
                cursor++;
            var word = s[start..cursor];
            if (cursor < s.Length && s[cursor] == '(')
            {
                cursor++; // '('
                var args = ParseArguments(s, ref cursor, ')');
                return new TscnValue(ValueKind.Call, text: word, items: args);
            }
            return word switch
            {
                "true" => new TscnValue(ValueKind.Bool, 1),
                "false" => new TscnValue(ValueKind.Bool, 0),
                "null" or "nil" => new TscnValue(ValueKind.Raw, text: word),
                "inf" => new TscnValue(ValueKind.Number, double.PositiveInfinity),
                _ => new TscnValue(ValueKind.Raw, text: word),
            };
        }

        // Anything else — consume the rest as raw.
        var rawStart = cursor;
        cursor = s.Length;
        return new TscnValue(ValueKind.Raw, text: s[rawStart..]);
    }

    private static TscnValue[] ParseArguments(string s, ref int cursor, char close)
    {
        var items = new List<TscnValue>();
        while (true)
        {
            while (cursor < s.Length && (char.IsWhiteSpace(s[cursor]) || s[cursor] == ','))
                cursor++;
            if (cursor >= s.Length)
                break;
            if (s[cursor] == close)
            {
                cursor++;
                break;
            }
            items.Add(ParseOne(s, ref cursor));
        }
        return items.ToArray();
    }

    private static string ParseString(string s, ref int cursor)
    {
        cursor++; // opening quote
        var sb = new StringBuilder();
        while (cursor < s.Length && s[cursor] != '"')
        {
            if (s[cursor] == '\\' && cursor + 1 < s.Length)
            {
                cursor++;
                sb.Append(s[cursor] switch { 'n' => '\n', 't' => '\t', var e => e });
            }
            else
            {
                sb.Append(s[cursor]);
            }
            cursor++;
        }
        cursor++; // closing quote
        return sb.ToString();
    }

    private static void ConsumeBalanced(string s, ref int cursor)
    {
        var depth = 0;
        var inString = false;
        while (cursor < s.Length)
        {
            var c = s[cursor];
            if (inString)
            {
                if (c == '\\')
                    cursor++;
                else if (c == '"')
                    inString = false;
            }
            else if (c == '"')
                inString = true;
            else if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    cursor++;
                    return;
                }
            }
            cursor++;
        }
    }
}
