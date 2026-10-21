using System.Text;

namespace Wabbajack.Merge;

public enum SkipReason
{
    NotSelected,
    Excluded,
    SharedWithBase,
    DuplicateOfBase,
    NoFiles
}

public sealed record AddedMod(string Name, string OriginalName, string? Separator, bool Enabled, int Files);

public sealed record SkippedMod(string Name, string? Separator, SkipReason Reason, string Detail);

public sealed record FileConflict(string Path, string Winner, IReadOnlyList<string> Losers);

/// <summary>Everything a user needs to check a merge without opening the result.</summary>
public sealed class MergeReport
{
    public string BaseName { get; set; } = "";
    public string OverlayName { get; set; } = "";
    public string OverlayProfile { get; set; } = "";
    public List<string> BaseProfiles { get; } = new();
    public List<AddedMod> Added { get; } = new();
    public List<SkippedMod> Skipped { get; } = new();
    public List<FileConflict> Conflicts { get; } = new();
    public List<string> IniSectionsAdded { get; } = new();
    public List<string> ArchiveLoadOrder { get; } = new();
    public List<string> Warnings { get; } = new();
    public int ArchivesAdded { get; set; }
    public long AddedDownloadSize { get; set; }
    public long TotalDownloadSize { get; set; }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Merge report: {BaseName} + {OverlayName}");
        sb.AppendLine();
        sb.AppendLine($"- Base profiles changed: {string.Join(", ", BaseProfiles)}");
        sb.AppendLine($"- Overlay profile used: {OverlayProfile}");
        sb.AppendLine($"- Overlay mods added: {Added.Count(a => !a.Name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))}");
        sb.AppendLine($"- Archives added: {ArchivesAdded} ({Gb(AddedDownloadSize)}); total download {Gb(TotalDownloadSize)}");
        sb.AppendLine();

        if (Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            foreach (var w in Warnings) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }

        sb.AppendLine("## Added from the overlay");
        sb.AppendLine();
        foreach (var a in Added)
        {
            var renamed = a.Name == a.OriginalName ? "" : $" (was {a.OriginalName})";
            var state = a.Enabled ? "" : " — disabled, as in the overlay";
            sb.AppendLine(a.Name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)
                ? $"\n**{a.Name[..^"_separator".Length]}**{renamed}"
                : $"- {a.Name}{renamed}{state}");
        }

        sb.AppendLine();
        foreach (var group in Skipped.GroupBy(s => s.Reason).OrderBy(g => g.Key))
        {
            sb.AppendLine(group.Key switch
            {
                SkipReason.SharedWithBase => "## In both lists — the base list's copy is kept",
                SkipReason.DuplicateOfBase => "## Same Nexus mod as a base mod under another name — skipped",
                SkipReason.Excluded => "## Excluded by the rules",
                SkipReason.NoFiles => "## Listed in the overlay profile but has no files",
                _ => "## Not selected"
            });
            sb.AppendLine();
            foreach (var s in group)
                sb.AppendLine($"- {s.Name}{(s.Separator == null ? "" : $" ({Sep(s.Separator)})")}{(s.Detail.Length == 0 ? "" : $": {s.Detail}")}");
            sb.AppendLine();
        }

        if (IniSectionsAdded.Count > 0)
        {
            sb.AppendLine("## Settings sections added");
            foreach (var i in IniSectionsAdded) sb.AppendLine($"- {i}");
            sb.AppendLine();
        }

        if (ArchiveLoadOrder.Count > 0)
        {
            sb.AppendLine($"## Archive load order ({ArchiveLoadOrder.Count} overlay archives load first, in this order)");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var a in ArchiveLoadOrder) sb.AppendLine(a);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine($"## Loose-file conflicts between the lists ({Conflicts.Count})");
        sb.AppendLine();
        if (Conflicts.Count == 0) sb.AppendLine("None.");
        foreach (var c in Conflicts)
            sb.AppendLine($"- `{c.Path}`: **{c.Winner}** wins over {string.Join(", ", c.Losers)}");
        return sb.ToString();
    }

    private static string Sep(string s) =>
        s.EndsWith("_separator", StringComparison.OrdinalIgnoreCase) ? s[..^"_separator".Length] : s;

    private static string Gb(long bytes) => $"{bytes / 1e9:0.00} GB";
}
