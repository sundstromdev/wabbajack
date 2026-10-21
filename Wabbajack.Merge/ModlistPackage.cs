using System.IO.Compression;
using Wabbajack.DTOs;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.Merge;

/// <summary>
///     A whole <c>.wabbajack</c> file held in memory: the modlist definition and every other zip entry
///     (inlined files, patches, the banner image). A modlist's own data is a few megabytes at most; the mods
///     themselves are never in it, so holding it all is cheaper than streaming it twice.
/// </summary>
public sealed class ModlistPackage
{
    public const string ModlistEntry = "modlist";
    private const string LegacyModlistEntry = "modlist.json";

    public ModlistPackage(ModList modList, Dictionary<string, byte[]>? entries = null)
    {
        ModList = modList;
        Entries = entries ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
    }

    public ModList ModList { get; }

    /// <summary>Every zip entry except the modlist definition, keyed by entry name.</summary>
    public Dictionary<string, byte[]> Entries { get; }

    public byte[] Data(RelativePath id)
    {
        if (!Entries.TryGetValue((string)id, out var data))
            throw new MergeException($"The modlist has no data entry named {id}");
        return data;
    }

    public static async Task<ModlistPackage> Load(DTOSerializer dtos, AbsolutePath path,
        CancellationToken token = default)
    {
        await using var fs = path.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        ModList? modList = null;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            await using var stream = entry.Open();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, token);
            if (entry.FullName is ModlistEntry or LegacyModlistEntry)
            {
                ms.Position = 0;
                modList = await dtos.DeserializeAsync<ModList>(ms, token);
                continue;
            }

            entries[entry.FullName] = ms.ToArray();
        }

        if (modList == null)
            throw new MergeException($"{path.FileName} is not a Wabbajack file: it has no modlist entry");
        return new ModlistPackage(modList, entries);
    }

    public async Task Save(DTOSerializer dtos, AbsolutePath path, CancellationToken token = default)
    {
        path.Parent.CreateDirectory();
        await using var fs = path.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        {
            var entry = zip.CreateEntry(ModlistEntry, CompressionLevel.Optimal);
            await using var os = entry.Open();
            await dtos.Serialize(ModList, os);
        }

        foreach (var (name, data) in Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            await using var os = entry.Open();
            await os.WriteAsync(data, token);
        }
    }
}
