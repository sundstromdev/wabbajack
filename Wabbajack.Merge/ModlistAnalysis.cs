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

    /// <summary>A profile's mods, lowest priority first.</summary>
    public List<Mo2Entry> ProfileAscending(string profile, out List<string> comments)
    {
        var d = ModList.Directives.FirstOrDefault(x => x.To == ProfileFile(profile, ModlistTxt));
        if (d is not InlineFile inline)
            throw new MergeException($"Profile {profile} of {ModList.Name} has no inlined modlist.txt");
        return Mo2ModList.ParseAscending(Mo2ModList.Decode(Package.Data(inline.SourceDataID)), out comments);
    }

    /// <summary>
    ///     Mod folders in this list made from the same Nexus mods as <paramref name="folder" /> of
    ///     <paramref name="other" />, when every file of it came from Nexus mods this list has. Empty when it is
    ///     not a duplicate.
    /// </summary>
    public List<string> DuplicatesOf(ModlistAnalysis other, string folder)
    {
        if (!other.NexusMods.TryGetValue(folder, out var keys) || keys.Count == 0 || other.HasNonNexusFiles[folder])
            return new List<string>();
        var mine = Folders.Keys.Where(f => NexusMods[f].Overlaps(keys)).ToList();
        var covered = mine.SelectMany(f => NexusMods[f]).ToHashSet();
        return keys.IsSubsetOf(covered) ? mine.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
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
