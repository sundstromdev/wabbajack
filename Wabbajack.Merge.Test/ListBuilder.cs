using System.Text;
using Wabbajack.DTOs;
using Wabbajack.DTOs.Directives;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;

namespace Wabbajack.Merge.Test;

/// <summary>Builds small Mod Organizer modlists in memory, shaped the way the compiler writes real ones.</summary>
internal sealed class ListBuilder
{
    private static long _next = 1000;
    private readonly List<Archive> _archives = new();
    private readonly List<Directive> _directives = new();
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);
    private readonly string _name;
    private Game _game = Game.Cyberpunk2077;

    public ListBuilder(string name) => _name = name;

    public ListBuilder ForGame(Game game)
    {
        _game = game;
        return this;
    }

    public Hash NexusArchive(string fileName, long modId)
    {
        var archive = new Archive
        {
            Hash = Hash.FromLong(Interlocked.Increment(ref _next)),
            Name = fileName,
            Size = 1000,
            State = new Nexus { Game = _game, ModID = modId, FileID = modId * 10 }
        };
        _archives.Add(archive);
        return archive.Hash;
    }

    public ListBuilder AddArchive(Archive archive)
    {
        _archives.Add(archive);
        return this;
    }

    /// <summary>A mod whose files all come from one Nexus archive. Files are paths inside the mod folder.</summary>
    public ListBuilder Mod(string folder, long modId, params string[] files) =>
        ModFrom(folder, NexusArchive(folder + ".zip", modId), files);

    public ListBuilder ModFrom(string folder, Hash archive, params string[] files)
    {
        Inline($@"mods\{folder}\meta.ini", "[General]\r\nmodid=0\r\n");
        foreach (var f in files) File($@"mods\{folder}\{f}", archive, f);
        return this;
    }

    public ListBuilder File(string to, Hash archive, string inArchive, long? hash = null)
    {
        _directives.Add(new FromArchive
        {
            To = (RelativePath)to,
            ArchiveHashPath = new HashRelativePath(archive, (RelativePath)inArchive),
            Hash = Hash.FromLong(hash ?? Interlocked.Increment(ref _next)),
            Size = 10
        });
        return this;
    }

    public ListBuilder Separator(string name) => Inline($@"mods\{name}\meta.ini", "[General]\r\ncolor=@Variant(0)\r\n");

    public ListBuilder Inline(string to, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var id = Guid.NewGuid().ToString();
        _entries[id] = data;
        _directives.Add(new InlineFile
        {
            To = (RelativePath)to, SourceDataID = (RelativePath)id, Hash = data.Hash().AsTask().Result,
            Size = data.Length
        });
        return this;
    }

    /// <summary>Lines lowest priority first, as Mod Organizer shows them: "+Mod", "-Mod", "+Name_separator".</summary>
    public ListBuilder Profile(string name, params string[] ascending)
    {
        var entries = ascending.Select(l => new Mo2Entry(l[0], l[1..]));
        return Inline($@"profiles\{name}\modlist.txt", Mo2ModList.WriteAscending(entries, Array.Empty<string>()));
    }

    public ModlistPackage Build() => new(new ModList
    {
        Name = _name,
        Author = _name + " author",
        GameType = _game,
        Archives = _archives.ToArray(),
        Directives = _directives.ToArray(),
        Version = new Version(1, 0, 0, 0)
    }, new Dictionary<string, byte[]>(_entries, StringComparer.Ordinal));
}

internal static class PackageExtensions
{
    public static string Text(this ModlistPackage p, string to)
    {
        var d = p.ModList.Directives.OfType<InlineFile>().Single(x => x.To == (RelativePath)to);
        return Encoding.UTF8.GetString(p.Data(d.SourceDataID));
    }

    public static List<string> ProfileLines(this ModlistPackage p, string profile) =>
        Mo2ModList.ParseAscending(p.Text($@"profiles\{profile}\modlist.txt"), out _).Select(e => e.ToString()).ToList();

    public static bool Installs(this ModlistPackage p, string to) =>
        p.ModList.Directives.Any(d => d.To == (RelativePath)to);

    public static Directive At(this ModlistPackage p, string to) =>
        p.ModList.Directives.Single(d => d.To == (RelativePath)to);
}
