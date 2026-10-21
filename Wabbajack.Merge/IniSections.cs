using System.Text.RegularExpressions;

namespace Wabbajack.Merge;

/// <summary>
///     Just enough ini handling to copy whole sections between files without disturbing either: a section is
///     its header line and every line up to the next header, kept verbatim.
/// </summary>
public static partial class IniSections
{
    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]\s*$")]
    private static partial Regex HeaderRegex();

    public static List<(string Name, string Text)> Split(string text)
    {
        var result = new List<(string, string)>();
        string? name = null;
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = HeaderRegex().Match(raw);
            if (m.Success)
            {
                if (name != null) result.Add((name, string.Join("\n", lines).TrimEnd('\n')));
                name = m.Groups["name"].Value.Trim();
                lines = new List<string> { raw };
                continue;
            }

            if (name != null) lines.Add(raw);
        }

        if (name != null) result.Add((name, string.Join("\n", lines).TrimEnd('\n')));
        return result;
    }

    /// <summary>
    ///     Appends to <paramref name="target" /> the sections of <paramref name="source" /> it does not have.
    ///     With <paramref name="only" /> non-empty, only those sections are considered; asking for one the source
    ///     does not have is an error, since it is almost always a typo. The target's line endings are kept.
    /// </summary>
    public static (string Text, List<string> Added) AppendMissing(string target, string source,
        IReadOnlyCollection<string> only)
    {
        var have = Split(target).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offered = Split(source);
        var offeredNames = offered.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = only.Where(o => !offeredNames.Contains(o)).ToList();
        if (unknown.Count > 0)
            throw new MergeException($"The source ini has no section(s) {string.Join(", ", unknown.Select(u => $"[{u}]"))}");

        var wanted = only.Count == 0 ? null : only.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = offered.Where(s => !have.Contains(s.Name) && (wanted == null || wanted.Contains(s.Name))).ToList();
        if (toAdd.Count == 0) return (target, new List<string>());

        var newline = target.Contains("\r\n") ? "\r\n" : "\n";
        var body = target.Replace("\r\n", "\n").TrimEnd('\n');
        var appended = string.Join("\n\n", toAdd.Select(s => s.Text));
        var merged = (body.Length == 0 ? appended : body + "\n\n" + appended) + "\n";
        return (merged.Replace("\n", newline), toAdd.Select(s => s.Name).ToList());
    }
}
