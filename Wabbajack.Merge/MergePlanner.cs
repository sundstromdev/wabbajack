using System.Text;
using Wabbajack.DTOs;

namespace Wabbajack.Merge;

/// <summary>
///     Drafts a rules file and a review list for a merge. Nothing here can tell a gameplay mod from a visual
///     one, so the draft takes every overlay mod the base does not already have and leaves the user to prune
///     it; the review list says, per overlay separator, which mods are new, shared or duplicates.
/// </summary>
public static class MergePlanner
{
    public static (MergeRules Rules, string Markdown) Draft(ModlistPackage basePackage, ModlistPackage overlayPackage,
        string? overlayProfile)
    {
        var b = new ModlistAnalysis(basePackage);
        var o = new ModlistAnalysis(overlayPackage);
        if (o.Profiles.Count == 0)
            throw new MergeException($"{o.ModList.Name} is not a Mod Organizer list (it has no profiles)");
        var profile = string.IsNullOrWhiteSpace(overlayProfile) ? o.Profiles.Max! : overlayProfile;
        if (!o.Profiles.Contains(profile))
            throw new MergeException($"{o.ModList.Name} has no profile {profile}. Its profiles: {string.Join(", ", o.Profiles)}");

        var rules = new MergeRules
        {
            OverlayProfile = profile,
            BaseProfiles = b.Profiles.ToList(),
            SeparatorPrefix = Initials(o.ModList.Name) + " ",
            InsertBefore = "REPLACE_WITH_A_BASE_ENTRY",
            CyberpunkArchiveLoadOrder = b.ModList.GameType == Game.Cyberpunk2077
                ? new CyberpunkArchiveLoadOrder { ModName = $"{Initials(o.ModList.Name)} Archive Load Order" }
                : null,
            Metadata = new MergeMetadata
            {
                Name = $"{b.ModList.Name} + {o.ModList.Name}",
                Version = "1.0.0"
            }
        };

        var sb = new StringBuilder();
        sb.AppendLine($"# Merge plan: {b.ModList.Name} (base) + {o.ModList.Name} (overlay)");
        sb.AppendLine();
        sb.AppendLine($"Overlay profile: **{profile}** (others: {string.Join(", ", o.Profiles.Where(p => p != profile))})");
        sb.AppendLine();
        sb.AppendLine("Edit the rules file: remove from `include.mods` what you do not want, and set `insertBefore` " +
                      "(or `insertAfter`) to one of the base entries listed at the end. " +
                      "`[new]` mods are in the draft; `[shared]` and `[duplicate]` ones are not, because the base list already has them.");
        sb.AppendLine();

        string? sep = null;
        foreach (var m in Analyse(b, o, profile))
        {
            if (m.Separator != sep)
            {
                sep = m.Separator;
                if (sep != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## {sep[..^"_separator".Length]}");
                }
            }

            var status = m.Status switch
            {
                OverlayModStatus.Shared => "[shared]",
                OverlayModStatus.NoFiles => "[no files]",
                OverlayModStatus.Duplicate => $"[duplicate of {string.Join(", ", m.DuplicateOf)}]",
                _ => "[new]"
            };
            if (m.Status == OverlayModStatus.New) rules.Include.Mods.Add(m.Name);
            sb.AppendLine($"- {status} {(m.Enabled ? "" : "(disabled) ")}{(m.NexusUrl == null ? m.Name : $"[{m.Name}]({m.NexusUrl})")}");
        }

        sb.AppendLine();
        sb.AppendLine($"## Base entries ({b.Profiles.Min}), lowest priority first");
        sb.AppendLine();
        foreach (var e in b.ProfileAscending(b.Profiles.Min!, out _).Where(e => e.IsSeparator))
            sb.AppendLine($"- `{e.Name}`");
        return (rules, sb.ToString());
    }

    /// <summary>Every overlay mod in the profile, lowest priority first, with what the base list already has of it.</summary>
    public static List<OverlayMod> Analyse(ModlistPackage basePackage, ModlistPackage overlayPackage, string overlayProfile) =>
        Analyse(new ModlistAnalysis(basePackage), new ModlistAnalysis(overlayPackage), overlayProfile);

    /// <summary>A base profile's entries, lowest priority first: the places an overlay block can go.</summary>
    public static List<Mo2Entry> BaseEntries(ModlistPackage basePackage, string profile) =>
        new ModlistAnalysis(basePackage).ProfileAscending(profile, out _);

    public static IReadOnlyCollection<string> Profiles(ModlistPackage package) => new ModlistAnalysis(package).Profiles;

    private static List<OverlayMod> Analyse(ModlistAnalysis b, ModlistAnalysis o, string profile)
    {
        var result = new List<OverlayMod>();
        string? sep = null;
        foreach (var e in o.ProfileAscending(profile, out _))
        {
            if (e.IsSeparator)
            {
                sep = e.Name;
                continue;
            }

            if (e.State == '*') continue;
            var dupes = new List<string>();
            var status = b.Folders.ContainsKey(e.Name) ? OverlayModStatus.Shared
                : !o.Folders.ContainsKey(e.Name) ? OverlayModStatus.NoFiles
                : (dupes = b.DuplicatesOf(o, e.Name)).Count > 0 ? OverlayModStatus.Duplicate
                : OverlayModStatus.New;
            result.Add(new OverlayMod(e.Name, sep, e.Enabled, status, dupes, o.NexusUrl(e.Name)));
        }

        return result;
    }

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
        var initials = new string(words.Select(w => char.ToUpperInvariant(w[0])).ToArray());
        return initials.Length is >= 2 and <= 4 ? initials : "Overlay";
    }
}

public enum OverlayModStatus
{
    /// <summary>Not in the base list in any form: what a merge is for.</summary>
    New,

    /// <summary>The base list has a mod folder of the same name; the base copy is always kept.</summary>
    Shared,

    /// <summary>Made from Nexus mods the base list already has, under another folder name.</summary>
    Duplicate,

    /// <summary>In the overlay profile but with no files in the list.</summary>
    NoFiles
}

public sealed record OverlayMod(string Name, string? Separator, bool Enabled, OverlayModStatus Status,
    IReadOnlyList<string> DuplicateOf, string? NexusUrl);
