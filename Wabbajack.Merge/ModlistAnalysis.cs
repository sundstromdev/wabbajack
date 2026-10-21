using Wabbajack.DTOs;
using Wabbajack.DTOs.Directives;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;

namespace Wabbajack.Merge;

/// <summary>
///     What a Mod Organizer based modlist looks like from the outside: its mod folders, its profiles and which
///     Nexus mods each folder's files came from. Built once per list.
/// </summary>
public sealed class ModlistAnalysis
{
    public const string ModsFolder = "mods";
    public const string ProfilesFolder = "profiles";
    public const string ModlistTxt = "modlist.txt";
    public const string MetaIni = "meta.ini";

    public ModlistAnalysis(ModlistPackage package)
    {
        Package = package;
        var byHash = new Dictionary<Hash, Archive>();
        foreach (var a in package.ModList.Archives) byHash.TryAdd(a.Hash, a);
        ArchivesByHash = byHash;

        foreach (var d in package.ModList.Directives)
        {
            if (TrySplitModPath(d.To, out var folder, out _))
            {
                if (!Folders.TryGetValue(folder, out var list))
                    Folders[folder] = list = new List<Directive>();
                list.Add(d);
            }
            else if (d.To.Depth == 3 && Eq(d.To.Parts[0], ProfilesFolder) && Eq(d.To.Parts[2], ModlistTxt))
            {
                Profiles.Add(d.To.Parts[1]);
            }
        }

        foreach (var (folder, directives) in Folders)
        {
            Paths[folder] = directives
                .Select(d => TrySplitModPath(d.To, out _, out var inside) ? (string)inside : "")
                .Where(p => p.Length > 0 && !Eq(p, MetaIni))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<NexusKey>();
            var fromElsewhere = false;
            foreach (var d in directives)
            {
                switch (d)
                {
                    case FromArchive fa:
                        if (ArchivesByHash.TryGetValue(fa.ArchiveHashPath.Hash, out var archive) &&
                            archive.State is Nexus n)
                            keys.Add(new NexusKey(n.Game, n.ModID));
                        else
                            fromElsewhere = true;
                        break;
                    case InlineFile when TrySplitModPath(d.To, out _, out var inside) && Eq((string)inside, MetaIni):
                        break;
                    default:
                        fromElsewhere = true;
                        break;
                }
            }

            NexusMods[folder] = keys;
            HasNonNexusFiles[folder] = fromElsewhere;
        }
    }

    public ModlistPackage Package { get; }
    public ModList ModList => Package.ModList;
    public IReadOnlyDictionary<Hash, Archive> ArchivesByHash { get; }

    public Dictionary<string, List<Directive>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<string> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<NexusKey>> NexusMods { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> HasNonNexusFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each mod folder's files, as paths inside the folder, without its meta.ini.</summary>
    public Dictionary<string, HashSet<string>> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A profile's mods, lowest priority first.</summary>
    public List<Mo2Entry> ProfileAscending(string profile, out List<string> comments)
    {
        var d = ModList.Directives.FirstOrDefault(x => x.To == ProfileFile(profile, ModlistTxt));
        if (d is not InlineFile inline)
            throw new MergeException($"Profile {profile} of {ModList.Name} has no inlined modlist.txt");
        return Mo2ModList.ParseAscending(Mo2ModList.Decode(Package.Data(inline.SourceDataID)), out comments);
    }

    /// <summary>
    ///     Mod folders in this list that are <paramref name="folder" /> of <paramref name="other" /> under another
    ///     name: folders made from a Nexus mod it is made from, holding at least half of its files at the same
    ///     paths. Empty when it is not a duplicate.
    ///
    ///     Neither test is enough alone, because Wabbajack shares identical files between archives. A mod that
    ///     bundles one helper script from a framework is recorded as taking it from the framework's archive, so
    ///     by Nexus id the framework would look installed; and a copy of a mod can be recorded as taking a file
    ///     from some third mod's archive, so requiring every id to match would miss it.
    /// </summary>
    public List<string> DuplicatesOf(ModlistAnalysis other, string folder)
    {
        if (!other.NexusMods.TryGetValue(folder, out var keys) || keys.Count == 0 || other.HasNonNexusFiles[folder])
            return new List<string>();
        var theirs = other.Paths[folder];
        if (theirs.Count == 0) return new List<string>();

        var mine = Folders.Keys.Where(f => NexusMods[f].Overlaps(keys) && Paths[f].Overlaps(theirs)).ToList();
        if (mine.Count == 0) return new List<string>();

        var present = theirs.Count(p => mine.Any(f => Paths[f].Contains(p)));
        return present * 2 >= theirs.Count
            ? mine.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
    }

    public string? NexusUrl(string folder)
    {
        if (!NexusMods.TryGetValue(folder, out var keys) || keys.Count != 1) return null;
        var k = keys.First();
        return $"https://www.nexusmods.com/{k.Game.MetaData().NexusDomain}/mods/{k.ModId}";
    }

    public static bool TrySplitModPath(RelativePath to, out string folder, out RelativePath inside)
    {
        if (to.Depth >= 3 && Eq(to.Parts[0], ModsFolder))
        {
            folder = to.Parts[1];
            inside = RelativePath.FromParts(to.Parts[2..]);
            return true;
        }

        folder = "";
        inside = default;
        return false;
    }

    public static RelativePath ModPath(string folder, RelativePath inside) =>
        RelativePath.FromParts(new[] { ModsFolder, folder }.Concat(inside.Parts).ToArray());

    public static RelativePath ProfileFile(string profile, string file) =>
        RelativePath.FromParts(new[] { ProfilesFolder, profile, file });

    public static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

public readonly record struct NexusKey(Game Game, long ModId);
