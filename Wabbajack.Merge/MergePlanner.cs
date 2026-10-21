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
        foreach (var e in o.ProfileAscending(profile, out _))
        {
            if (e.IsSeparator)
            {
                sep = e.Name;
                sb.AppendLine();
                sb.AppendLine($"## {e.Name[..^"_separator".Length]}");
                continue;
            }

            if (e.State == '*') continue;
            string status;
            if (b.Folders.ContainsKey(e.Name)) status = "[shared]";
            else if (!o.Folders.ContainsKey(e.Name)) status = "[no files]";
            else if (b.DuplicatesOf(o, e.Name) is { Count: > 0 } d) status = $"[duplicate of {string.Join(", ", d)}]";
            else
            {
                status = "[new]";
                rules.Include.Mods.Add(e.Name);
            }

            var url = o.NexusUrl(e.Name);
            sb.AppendLine($"- {status} {(e.Enabled ? "" : "(disabled) ")}{(url == null ? e.Name : $"[{e.Name}]({url})")}");
        }

        sb.AppendLine();
        sb.AppendLine($"## Base entries ({b.Profiles.Min}), lowest priority first");
        sb.AppendLine();
        foreach (var e in b.ProfileAscending(b.Profiles.Min!, out _).Where(e => e.IsSeparator))
            sb.AppendLine($"- `{e.Name}`");
        return (rules, sb.ToString());
    }

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
        var initials = new string(words.Select(w => char.ToUpperInvariant(w[0])).ToArray());
        return initials.Length is >= 2 and <= 4 ? initials : "Overlay";
    }
}
