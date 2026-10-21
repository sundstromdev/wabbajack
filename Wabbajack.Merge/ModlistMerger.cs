using System.Reflection;
using Wabbajack.DTOs;
using Wabbajack.DTOs.Directives;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;
using static Wabbajack.Merge.ModlistAnalysis;

namespace Wabbajack.Merge;

public sealed record MergeResult(ModlistPackage Output, MergeReport Report);

/// <summary>
///     Puts chosen mods of one Mod Organizer modlist (the overlay) into another (the base) and writes a
///     modlist Wabbajack installs like any other.
///
///     The base list is the authority. None of its directives, archives, profiles' existing entries or
///     their order change; the overlay's mods go in as one block at a place the rules name, in the
///     overlay's own order with its enabled state, and a mod folder both lists have is the base's. Only two
///     kinds of base file are rewritten: the profiles' <c>modlist.txt</c> (to add the block) and inis the
///     rules ask to add settings sections to.
///
///     Plugin load order (Bethesda games) is not merged, so lists with a <c>plugins.txt</c> are refused.
/// </summary>
public static class ModlistMerger
{
    private static readonly Type[] CopyableDirectives =
        { typeof(FromArchive), typeof(PatchedFromArchive), typeof(InlineFile), typeof(RemappedInlineFile) };

    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static async Task<MergeResult> Merge(ModlistPackage basePackage, ModlistPackage overlayPackage,
        MergeRules rules)
    {
        var b = new ModlistAnalysis(basePackage);
        var o = new ModlistAnalysis(overlayPackage);
        var report = new MergeReport
        {
            BaseName = b.ModList.Name, OverlayName = o.ModList.Name, OverlayProfile = rules.OverlayProfile
        };

        var targetProfiles = CheckPreconditions(b, o, rules);
        report.BaseProfiles.AddRange(targetProfiles);

        var overlayAsc = o.ProfileAscending(rules.OverlayProfile, out _);
        CheckNames(b, o, rules, overlayAsc, targetProfiles);

        var selected = Select(b, o, rules, overlayAsc, report);
        var block = BuildBlock(rules, selected, out var separators);
        CheckBlockNames(b, targetProfiles, block, rules);

        var entries = new Dictionary<string, byte[]>(basePackage.Entries, StringComparer.Ordinal);
        var output = new List<Directive>(b.ModList.Directives);
        var written = new List<Directive>();

        async Task<InlineFile> NewInline(RelativePath to, byte[] data)
        {
            var id = Guid.NewGuid().ToString();
            entries[id] = data;
            var d = new InlineFile { To = to, SourceDataID = (RelativePath)id, Hash = await data.Hash(), Size = data.Length };
            written.Add(d);
            return d;
        }

        void Replace(Directive old, Directive replacement) => output[output.IndexOf(old)] = replacement;

        // Mods and separators from the overlay.
        var problems = new List<string>();
        foreach (var s in selected)
        {
            var newName = NewModName(rules, s.Entry.Name);
            var copied = 0;
            foreach (var d in o.Folders[s.Entry.Name])
            {
                TrySplitModPath(d.To, out _, out var inside);
                if (!PassesFilter(rules, s.Entry.Name, inside)) continue;
                if (!CopyableDirectives.Contains(d.GetType()))
                {
                    problems.Add($"{s.Entry.Name} contains {d.GetType().Name} directives ({d.To}), which cannot be merged");
                    continue;
                }

                output.Add(CopyDirective(d, ModPath(newName, inside), overlayPackage, entries));
                copied++;
            }

            report.Added.Add(new AddedMod(newName, s.Entry.Name, s.Separator, s.Entry.Enabled, copied));
        }

        foreach (var (newSep, oldSep) in separators)
        {
            if (o.Folders.TryGetValue(oldSep, out var sepDirectives))
                foreach (var d in sepDirectives)
                {
                    TrySplitModPath(d.To, out _, out var inside);
                    output.Add(CopyDirective(d, ModPath(newSep, inside), overlayPackage, entries));
                }
            else
                output.Add(await NewInline(ModPath(newSep, (RelativePath)MetaIni), Mo2ModList.Encode(MetaIniText(""))));
        }

        // Settings sections.
        foreach (var append in rules.IniSectionAppends)
        {
            var from = o.ModList.Directives.FirstOrDefault(d => d.To == (RelativePath)append.From);
            if (from is not InlineFile fromInline)
            {
                problems.Add($"iniSectionAppends: {append.From} is not an inlined file of {o.ModList.Name}");
                continue;
            }

            var source = Mo2ModList.Decode(overlayPackage.Data(fromInline.SourceDataID));
            foreach (var to in append.To)
            {
                var target = output.FirstOrDefault(d => d.To == (RelativePath)to);
                if (target is null || target.GetType() != typeof(InlineFile))
                {
                    problems.Add($"iniSectionAppends: {to} is not an inlined file of {b.ModList.Name}");
                    continue;
                }

                var text = Mo2ModList.Decode(entries[(string)((InlineFile)target).SourceDataID]);
                try
                {
                    var (appended, added) = IniSections.AppendMissing(text, source, append.Sections);
                    if (added.Count == 0) continue;
                    Replace(target, await NewInline(target.To, Mo2ModList.Encode(appended)));
                    report.IniSectionsAdded.Add($"{to}: {string.Join(", ", added.Select(a => $"[{a}]"))}");
                }
                catch (MergeException e)
                {
                    problems.Add($"iniSectionAppends {append.From}: {e.Message}");
                }
            }
        }

        // Cyberpunk archive load order, as a mod of its own at the end of the block.
        if (rules.CyberpunkArchiveLoadOrder is { } lo)
        {
            var order = ArchiveLoadOrder(o, rules, selected, report);
            report.ArchiveLoadOrder.AddRange(order);
            output.Add(await NewInline(ModPath(lo.ModName, (RelativePath)MetaIni),
                Mo2ModList.Encode(MetaIniText($"Loads the archives of {o.ModList.Name} first, in its order. " +
                                              "Every other archive loads alphabetically after them, as before."))));
            output.Add(await NewInline(ModPath(lo.ModName, (RelativePath)@"archive\pc\mod\modlist.txt"),
                Mo2ModList.Encode(string.Join("\n", order))));
            block.Add(new Mo2Entry('+', lo.ModName));
            report.Added.Add(new AddedMod(lo.ModName, lo.ModName, null, true, 2));

            var existing = b.Folders.Where(f => f.Value.Any(d =>
                TrySplitModPath(d.To, out _, out var inside) && Eq((string)inside, @"archive\pc\mod\modlist.txt")));
            foreach (var e in existing)
                report.Warnings.Add($"{e.Key} in the base list also ships archive\\pc\\mod\\modlist.txt; whichever has priority decides the archive order");
        }

        // The block goes into each base profile; nothing already there moves.
        List<Mo2Entry>? firstProfile = null;
        foreach (var profile in targetProfiles)
        {
            var path = ProfileFile(profile, ModlistTxt);
            var directive = output.First(d => d.To == path);
            var asc = b.ProfileAscending(profile, out var comments);
            var anchor = rules.InsertBefore ?? rules.InsertAfter!;
            var at = Mo2ModList.IndexOf(asc, anchor);
            if (at < 0)
            {
                problems.Add($"Base profile {profile} has no entry {anchor} to insert {(rules.InsertBefore != null ? "before" : "after")}");
                continue;
            }

            asc.InsertRange(rules.InsertBefore != null ? at : at + 1, block);
            Replace(directive, await NewInline(path, Mo2ModList.Encode(Mo2ModList.WriteAscending(asc, comments))));
            firstProfile ??= asc;
        }

        // Archives the copied directives need.
        var archives = b.ModList.Archives.ToList();
        var have = archives.Select(a => a.Hash).ToHashSet();
        var namesInBase = archives.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Hash, StringComparer.OrdinalIgnoreCase);
        var needed = output.OfType<FromArchive>().Select(d => d.ArchiveHashPath.Hash).ToHashSet();
        foreach (var hash in needed.Where(h => !have.Contains(h)))
        {
            if (!o.ArchivesByHash.TryGetValue(hash, out var archive))
            {
                problems.Add($"No archive with hash {hash} in either list");
                continue;
            }

            if (namesInBase.TryGetValue(archive.Name, out var other) && other != hash)
                problems.Add($"Both lists download a file named {archive.Name}, but they are different files");
            archives.Add(archive);
            have.Add(hash);
            report.ArchivesAdded++;
            report.AddedDownloadSize += archive.Size;
        }

        report.TotalDownloadSize = archives.Sum(a => a.Size);
        if (problems.Count > 0) throw new MergeException(problems);

        var position = block.Select((e, i) => (e.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
        report.Added.Sort((x, y) => position[x.Name].CompareTo(position[y.Name]));

        var merged = new ModList
        {
            Archives = archives.ToArray(),
            Directives = output.ToArray(),
            Author = rules.Metadata.Author ?? $"{b.ModList.Author} + {o.ModList.Author}",
            Description = rules.Metadata.Description ??
                          $"{b.ModList.Name} with mods from {o.ModList.Name} ({rules.OverlayProfile}). Unofficial merge.",
            Name = rules.Metadata.Name ?? $"{b.ModList.Name} + {o.ModList.Name}",
            Version = rules.Metadata.Version != null ? ParseVersion(rules.Metadata.Version) : b.ModList.Version,
            GameType = b.ModList.GameType,
            OtherGames = b.ModList.OtherGames.Concat(o.ModList.OtherGames).Distinct().ToArray(),
            Image = b.ModList.Image,
            Readme = b.ModList.Readme,
            Website = b.ModList.Website,
            Community = b.ModList.Community,
            WabbajackVersion = b.ModList.WabbajackVersion,
            IsNSFW = b.ModList.IsNSFW || o.ModList.IsNSFW
        };
        var package = new ModlistPackage(merged, entries);

        if (firstProfile != null)
            report.Conflicts.AddRange(FindConflicts(merged, firstProfile, report.Added.Select(a => a.Name)));

        var invalid = await PackageValidator.Validate(package, written);
        if (invalid.Count > 0) throw new MergeException(invalid);
        return new MergeResult(package, report);
    }

    private sealed record Selected(Mo2Entry Entry, string? Separator);

    private static List<string> CheckPreconditions(ModlistAnalysis b, ModlistAnalysis o, MergeRules rules)
    {
        var problems = new List<string>();
        if (b.ModList.GameType != o.ModList.GameType)
            problems.Add($"{b.ModList.Name} is for {b.ModList.GameType} and {o.ModList.Name} for {o.ModList.GameType}");

        foreach (var list in new[] { b, o })
        {
            if (list.Profiles.Count == 0)
                problems.Add($"{list.ModList.Name} is not a Mod Organizer list (it has no profiles\\*\\modlist.txt)");
            var plugins = list.ModList.Directives.FirstOrDefault(d => d.To.Depth == 3 && Eq(d.To.Parts[0], ProfilesFolder) &&
                                                                      (Eq(d.To.Parts[2], "plugins.txt") || Eq(d.To.Parts[2], "loadorder.txt")));
            if (plugins != null)
                problems.Add($"{list.ModList.Name} has a plugin load order ({plugins.To}). Merging plugin load orders is not supported yet, " +
                             "so only lists without plugins, such as Cyberpunk 2077 lists, can be merged");
        }

        if (string.IsNullOrWhiteSpace(rules.OverlayProfile))
            problems.Add($"Set overlayProfile to one of: {string.Join(", ", o.Profiles)}");
        else if (!o.Profiles.Contains(rules.OverlayProfile))
            problems.Add($"{o.ModList.Name} has no profile {rules.OverlayProfile}. Its profiles: {string.Join(", ", o.Profiles)}");

        var targets = rules.BaseProfiles.Count == 0 ? b.Profiles.ToList() : rules.BaseProfiles;
        foreach (var p in targets.Where(p => !b.Profiles.Contains(p)))
            problems.Add($"{b.ModList.Name} has no profile {p}. Its profiles: {string.Join(", ", b.Profiles)}");

        if ((rules.InsertBefore == null) == (rules.InsertAfter == null))
            problems.Add("Set exactly one of insertBefore and insertAfter to the base mod or separator the overlay block goes next to");

        if (rules.CyberpunkArchiveLoadOrder != null && b.ModList.GameType != Game.Cyberpunk2077)
            problems.Add("cyberpunkArchiveLoadOrder only applies to Cyberpunk 2077 lists");

        if (problems.Count > 0) throw new MergeException(problems);
        return targets;
    }

    /// <summary>Every name in the rules has to mean something; a typo would otherwise silently do nothing.</summary>
    private static void CheckNames(ModlistAnalysis b, ModlistAnalysis o, MergeRules rules, List<Mo2Entry> overlayAsc,
        List<string> targetProfiles)
    {
        var problems = new List<string>();
        var mods = overlayAsc.Where(e => !e.IsSeparator).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seps = overlayAsc.Where(e => e.IsSeparator).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Need(IEnumerable<string> names, HashSet<string> set, string what, string field)
        {
            foreach (var n in names.Where(n => !set.Contains(n)))
                problems.Add($"{field}: {n} is not a {what} in overlay profile {rules.OverlayProfile}");
        }

        Need(rules.Include.Mods, mods, "mod", "include.mods");
        Need(rules.Include.Separators, seps, "separator", "include.separators");
        Need(rules.Exclude, mods, "mod", "exclude");
        Need(rules.ModRenames.Keys, mods, "mod", "modRenames");
        Need(rules.FileFilters.Keys, mods, "mod", "fileFilters");
        Need(rules.SeparatorRenames.Keys, seps, "separator", "separatorRenames");

        var baseNames = new HashSet<string>(b.Folders.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var p in targetProfiles)
            foreach (var e in b.ProfileAscending(p, out _))
                baseNames.Add(e.Name);

        foreach (var (from, to) in rules.ModRenames.Where(r => baseNames.Contains(r.Value)))
            problems.Add($"modRenames: {from} cannot become {to}, the base list already has a mod by that name");
        foreach (var dupe in rules.ModRenames.Values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"modRenames: more than one mod is renamed to {dupe.Key}");
        foreach (var (from, to) in rules.SeparatorRenames.Where(r => r.Value != null && !r.Value.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)))
            problems.Add($"separatorRenames: {to} (for {from}) must end in _separator");
        if (rules.CyberpunkArchiveLoadOrder is { } lo && (baseNames.Contains(lo.ModName) || mods.Contains(lo.ModName)))
            problems.Add($"cyberpunkArchiveLoadOrder.modName {lo.ModName} is already a mod name");

        if (problems.Count > 0) throw new MergeException(problems);
    }

    private static List<Selected> Select(ModlistAnalysis b, ModlistAnalysis o, MergeRules rules,
        List<Mo2Entry> overlayAsc, MergeReport report)
    {
        var explicitMods = rules.Include.Mods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var separators = rules.Include.Separators.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = rules.Exclude.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<Selected>();
        string? sep = null;
        foreach (var e in overlayAsc)
        {
            if (e.IsSeparator)
            {
                sep = e.Name;
                continue;
            }

            if (e.State == '*') continue;
            var byName = explicitMods.Contains(e.Name);
            if (!byName && !(sep != null && separators.Contains(sep)))
            {
                report.Skipped.Add(new SkippedMod(e.Name, sep, SkipReason.NotSelected, ""));
                continue;
            }

            if (excluded.Contains(e.Name))
            {
                report.Skipped.Add(new SkippedMod(e.Name, sep, SkipReason.Excluded, ""));
                continue;
            }

            if (b.Folders.ContainsKey(e.Name))
            {
                report.Skipped.Add(new SkippedMod(e.Name, sep, SkipReason.SharedWithBase, ""));
                continue;
            }

            if (!o.Folders.ContainsKey(e.Name))
            {
                report.Skipped.Add(new SkippedMod(e.Name, sep, SkipReason.NoFiles, ""));
                continue;
            }

            var dupes = b.DuplicatesOf(o, e.Name);
            if (dupes.Count > 0)
            {
                if (rules.SkipDuplicatesOfBase && !byName)
                {
                    report.Skipped.Add(new SkippedMod(e.Name, sep, SkipReason.DuplicateOfBase, "base has " + string.Join(", ", dupes)));
                    continue;
                }

                report.Warnings.Add($"{e.Name} is the same Nexus mod as {string.Join(", ", dupes)} in the base list; taken because include.mods names it");
            }

            result.Add(new Selected(e, sep));
        }

        return result;
    }

    /// <summary>Mod Organizer has one folder per name, so nothing in the block may share a name with the base.</summary>
    private static void CheckBlockNames(ModlistAnalysis b, List<string> targetProfiles, List<Mo2Entry> block, MergeRules rules)
    {
        var baseNames = new HashSet<string>(b.Folders.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var p in targetProfiles)
            foreach (var e in b.ProfileAscending(p, out _))
                baseNames.Add(e.Name);

        var problems = block.Where(e => baseNames.Contains(e.Name))
            .Select(e => e.IsSeparator
                ? $"The base list already has a separator {e.Name}; set separatorPrefix or separatorRenames"
                : $"The base list already has a mod {e.Name}; rename the overlay's with modRenames")
            .ToList();
        problems.AddRange(block.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => $"More than one overlay entry ends up named {g.Key}"));
        if (rules.CyberpunkArchiveLoadOrder is { } lo && block.Any(e => Eq(e.Name, lo.ModName)))
            problems.Add($"cyberpunkArchiveLoadOrder.modName {lo.ModName} is already used by an overlay mod");
        if (problems.Count > 0) throw new MergeException(problems);
    }

    private static List<Mo2Entry> BuildBlock(MergeRules rules, List<Selected> selected,
        out List<(string NewSep, string OldSep)> separators)
    {
        var block = new List<Mo2Entry>();
        separators = new List<(string, string)>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var s in selected)
        {
            if (s.Separator != null && !Eq(s.Separator, last ?? ""))
            {
                last = s.Separator;
                var newSep = rules.SeparatorRenames.TryGetValue(s.Separator, out var renamed)
                    ? renamed
                    : rules.SeparatorPrefix + s.Separator;
                if (newSep != null && emitted.Add(newSep))
                {
                    block.Add(new Mo2Entry('+', newSep));
                    separators.Add((newSep, s.Separator));
                }
            }

            block.Add(new Mo2Entry(s.Entry.State, NewModName(rules, s.Entry.Name)));
        }

        return block;
    }

    private static List<string> ArchiveLoadOrder(ModlistAnalysis o, MergeRules rules, List<Selected> selected,
        MergeReport report)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in selected.Where(s => s.Entry.Enabled))
        {
            var names = o.Folders[s.Entry.Name]
                .Select(d => TrySplitModPath(d.To, out _, out var inside) ? inside : default)
                .Where(inside => inside.Depth == 4 && Eq(inside.Parts[0], "archive") && Eq(inside.Parts[1], "pc") &&
                                 Eq(inside.Parts[2], "mod") && inside.Parts[3].EndsWith(".archive", StringComparison.OrdinalIgnoreCase) &&
                                 PassesFilter(rules, s.Entry.Name, inside))
                .Select(inside => inside.Parts[3])
                .OrderBy(n => n.ToUpperInvariant(), StringComparer.Ordinal);
            foreach (var n in names)
                if (seen.Add(n)) order.Add(n);
                else report.Warnings.Add($"Archive {n} is shipped by more than one overlay mod; the first in load order is used");
        }

        return order;
    }

    private static IEnumerable<FileConflict> FindConflicts(ModList merged, List<Mo2Entry> profileAsc,
        IEnumerable<string> overlayMods)
    {
        var priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < profileAsc.Count; i++)
            if (profileAsc[i].Enabled) priority[profileAsc[i].Name] = i;
        var fromOverlay = overlayMods.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byPath = new Dictionary<string, List<(string Folder, Hash Hash)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in merged.Directives)
        {
            if (!TrySplitModPath(d.To, out var folder, out var inside) || !priority.ContainsKey(folder)) continue;
            var key = (string)inside;
            if (Eq(key, MetaIni)) continue;
            if (!byPath.TryGetValue(key, out var list)) byPath[key] = list = new();
            list.Add((folder, d.Hash));
        }

        foreach (var (path, providers) in byPath.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (providers.Count < 2 || providers.Select(p => p.Hash).Distinct().Count() < 2) continue;
            if (!providers.Any(p => fromOverlay.Contains(p.Folder)) || providers.All(p => fromOverlay.Contains(p.Folder))) continue;
            var ordered = providers.OrderByDescending(p => priority[p.Folder]).Select(p => p.Folder).ToList();
            yield return new FileConflict(path, ordered[0], ordered.Skip(1).ToList());
        }
    }

    private static Directive CopyDirective(Directive source, RelativePath to, ModlistPackage from,
        Dictionary<string, byte[]> entries)
    {
        var copy = (Directive)CloneMethod.Invoke(source, null)!;
        copy.To = to;
        switch (copy)
        {
            case InlineFile inline:
                inline.SourceDataID = CopyData(inline.SourceDataID, from, entries);
                break;
            case PatchedFromArchive patched:
                patched.PatchID = CopyData(patched.PatchID, from, entries);
                break;
        }

        return copy;
    }

    /// <summary>Data ids are GUIDs, so they almost never meet; if one does with different bytes it gets a new id.</summary>
    private static RelativePath CopyData(RelativePath id, ModlistPackage from, Dictionary<string, byte[]> entries)
    {
        var data = from.Data(id);
        var key = (string)id;
        if (entries.TryGetValue(key, out var existing) && !existing.AsSpan().SequenceEqual(data))
            key = Guid.NewGuid().ToString();
        entries[key] = data;
        return (RelativePath)key;
    }

    private static bool PassesFilter(MergeRules rules, string mod, RelativePath inside)
    {
        var path = (string)inside;
        if (Eq(path, MetaIni) || !rules.FileFilters.TryGetValue(mod, out var filter)) return true;
        bool Matches(string prefix) => path.StartsWith(prefix.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        if (filter.Keep.Count > 0 && !filter.Keep.Any(Matches)) return false;
        return !filter.Drop.Any(Matches);
    }

    private static string NewModName(MergeRules rules, string name) =>
        rules.ModRenames.TryGetValue(name, out var renamed) ? renamed : name;

    private static Version ParseVersion(string text) =>
        Version.TryParse(text, out var v) ? v : throw new MergeException($"metadata.version {text} is not a version such as 1.0.0");

    private static string MetaIniText(string comment) =>
        string.Join("\r\n", "[General]", "modid=0", "version=", "newestVersion=", "category=\"\"", "installationFile=",
            "repository=Nexus", "gameName=", $"comments={comment}", "notes=", "nexusDescription=", "url=",
            "hasCustomURL=false", "nexusFileStatus=1", "lastNexusQuery=", "lastNexusUpdate=", "nexusLastModified=",
            "nexusCategory=0", "converted=false", "validated=false", "tracked=0", "", "[installedFiles]", "size=0", "");
}
