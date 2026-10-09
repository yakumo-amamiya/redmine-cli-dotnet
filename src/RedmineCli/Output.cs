using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RedmineCli;

/// <summary>A column of <see cref="Output.Table{T}"/>.</summary>
internal sealed record Column<T>(string Label, Func<T, string?> Value, bool Right = false, int Max = 0);

/// <summary>
/// The output contract:
/// - results (tables, JSON) go to stdout;
/// - progress, confirmation, warnings and errors go to stderr. With --json, stdout carries nothing but the JSON.
/// </summary>
internal static partial class Output
{
    public static void Out(string line = "") => Console.Out.Write(line + "\n");

    public static void Info(string line = "") => Console.Error.Write(line + "\n");

    public static void PrintJson(JsonNode? value) => Console.Out.Write(Json.Format(value) + "\n");

    /// <summary>Full-width characters count as two columns (for aligning tables).</summary>
    private static bool IsWide(int c) => c is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0x303E) or (>= 0x3041 and <= 0x33FF)
        or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA000 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
        or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6)
        or (>= 0x20000 and <= 0x3FFFD);

    public static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }
        return width;
    }

    private static string Pad(string text, int width, bool right)
    {
        var gap = Math.Max(0, width - DisplayWidth(text));
        return right ? new string(' ', gap) + text : text + new string(' ', gap);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>One line (runs of whitespace become one space) no wider than max, ending with … when cut.</summary>
    public static string Truncate(string? text, int max)
    {
        var s = Whitespace().Replace(text ?? "", " ");
        if (DisplayWidth(s) <= max)
        {
            return s;
        }
        var result = new StringBuilder();
        var width = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            var w = IsWide(rune.Value) ? 2 : 1;
            if (width + w > max - 1)
            {
                break;
            }
            result.Append(rune.ToString());
            width += w;
        }
        return result + "…";
    }

    public static string Table<T>(IReadOnlyList<T> rows, params Column<T>[] columns)
    {
        var cells = rows
            .Select(row => columns.Select(column =>
            {
                var text = column.Value(row) ?? "";
                return column.Max > 0 ? Truncate(text, column.Max) : text;
            }).ToArray())
            .ToList();
        var widths = columns
            .Select((column, i) => Math.Max(DisplayWidth(column.Label), cells.Count == 0 ? 0 : cells.Max(r => DisplayWidth(r[i]))))
            .ToArray();
        var lines = new List<string>
        {
            string.Join("  ", columns.Select((column, i) => Pad(column.Label, widths[i], column.Right))).TrimEnd(),
            string.Join("  ", widths.Select(w => new string('-', w))),
        };
        foreach (var row in cells)
        {
            lines.Add(string.Join("  ", row.Select((cell, i) => Pad(cell, widths[i], columns[i].Right))).TrimEnd());
        }
        return string.Join("\n", lines);
    }

    public static string FormatSize(JsonNode? bytes)
    {
        if (bytes.Double() is not { } value)
        {
            return "";
        }
        return FormatSize(value);
    }

    public static string FormatSize(double value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        var number = index == 0 ? value.ToString(CultureInfo.InvariantCulture) : value.ToString("F1", CultureInfo.InvariantCulture);
        return $"{number} {units[index]}";
    }

    [GeneratedRegex(@"Z$|[+-]\d\d:\d\d$")]
    private static partial Regex Zone();

    /// <summary>"2026-09-01T09:30:00Z" → "2026-09-01 09:30".</summary>
    public static string ShortDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso))
        {
            return "";
        }
        var t = iso.IndexOf('T');
        var s = t < 0 ? iso : string.Concat(iso.AsSpan(0, t), " ", iso.AsSpan(t + 1));
        s = Zone().Replace(s, "", 1);
        return s.Length > 16 ? s[..16] : s;
    }

    public static string ShortDate(JsonNode? iso) => ShortDate(iso.Str());
}
