using System.Text;

namespace Wabbajack.Merge;

/// <summary>One line of a Mod Organizer <c>modlist.txt</c>: <c>+Name</c> enabled, <c>-Name</c> disabled, <c>*Name</c> unmanaged.</summary>
public sealed record Mo2Entry(char State, string Name)
{
    public bool Enabled => State == '+';
    public bool IsSeparator => Name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => State + Name;
}

/// <summary>
///     Reads and writes a profile's <c>modlist.txt</c>. The file lists the highest priority mod first; everything
///     here works in the order Mod Organizer shows, lowest priority first, which is also the order the
///     Cyberpunk support plugin writes archive load order in.
/// </summary>
public static class Mo2ModList
{
    public static List<Mo2Entry> ParseAscending(string text, out List<string> comments)
    {
        comments = new List<string>();
        var entries = new List<Mo2Entry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                comments.Add(line);
                continue;
            }

            if (line[0] is '+' or '-' or '*')
                entries.Add(new Mo2Entry(line[0], line[1..]));
            else
                entries.Add(new Mo2Entry('+', line));
        }

        entries.Reverse();
        return entries;
    }

    public static string WriteAscending(IEnumerable<Mo2Entry> ascending, IEnumerable<string> comments)
    {
        var lines = comments.Concat(ascending.Reverse().Select(e => e.ToString()));
        return string.Join("\r\n", lines);
    }

    public static int IndexOf(IReadOnlyList<Mo2Entry> entries, string name)
    {
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public static byte[] Encode(string text) => new UTF8Encoding(false).GetBytes(text);
    public static string Decode(byte[] data) => new UTF8Encoding(false).GetString(data).TrimStart('﻿');
}
