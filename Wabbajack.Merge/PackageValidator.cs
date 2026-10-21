using Wabbajack.DTOs;
using Wabbajack.DTOs.Directives;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;

namespace Wabbajack.Merge;

/// <summary>
///     The checks an installer would otherwise fail on halfway through a download: every file installed once,
///     every archive and every inlined file present, and the hashes of anything this merge wrote.
/// </summary>
public static class PackageValidator
{
    public static async Task<List<string>> Validate(ModlistPackage package, IEnumerable<Directive> written)
    {
        var problems = new List<string>();
        var list = package.ModList;

        foreach (var dupe in list.Directives.GroupBy(d => d.To).Where(g => g.Count() > 1))
            problems.Add($"{dupe.Key} is installed by {dupe.Count()} directives");

        var archives = list.Archives.Select(a => a.Hash).ToHashSet();
        foreach (var d in list.Directives.OfType<FromArchive>())
            if (!archives.Contains(d.ArchiveHashPath.Hash))
                problems.Add($"{d.To} comes from an archive the list does not download ({d.ArchiveHashPath.Hash})");

        foreach (var d in list.Directives)
        {
            if (d is InlineFile inline && !package.Entries.ContainsKey((string)inline.SourceDataID))
                problems.Add($"{d.To} has no inlined data ({inline.SourceDataID})");
            if (d is PatchedFromArchive patched && !package.Entries.ContainsKey((string)patched.PatchID))
                problems.Add($"{d.To} has no patch data ({patched.PatchID})");
        }

        foreach (var d in written.OfType<InlineFile>())
        {
            if (!package.Entries.TryGetValue((string)d.SourceDataID, out var data)) continue;
            if (data.Length != d.Size || await data.Hash() != d.Hash)
                problems.Add($"{d.To} does not match its recorded hash or size");
        }

        var names = list.Archives.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(a => a.Hash).Distinct().Count() > 1);
        foreach (var n in names)
            problems.Add($"Two different archives are both named {n.Key}; Wabbajack keys downloads by name");

        return problems;
    }
}
