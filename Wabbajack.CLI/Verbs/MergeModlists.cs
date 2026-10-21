using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Merge;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.CLI.Verbs;

public class MergeModlists
{
    private readonly ILogger<MergeModlists> _logger;
    private readonly DTOSerializer _dtos;

    public MergeModlists(ILogger<MergeModlists> logger, DTOSerializer dtos)
    {
        _logger = logger;
        _dtos = dtos;
    }

    public static VerbDefinition Definition = new("merge-modlists",
        "Merges chosen mods of an overlay Wabbajack file into a base one, keeping the base list's mods, settings and order. " +
        "Writes a new .wabbajack and a markdown report beside it. Start from the rules plan-merge drafts.",
        [
            new OptionDefinition(typeof(AbsolutePath), "b", "base", "Base Wabbajack file: everything in it is kept as it is"),
            new OptionDefinition(typeof(AbsolutePath), "ol", "overlay", "Overlay Wabbajack file to take mods from"),
            new OptionDefinition(typeof(AbsolutePath), "r", "rules", "Merge rules (JSON)"),
            new OptionDefinition(typeof(AbsolutePath), "o", "output", "Wabbajack file to write")
        ]);

    internal async Task<int> Run(AbsolutePath @base, AbsolutePath overlay, AbsolutePath rules, AbsolutePath output)
    {
        foreach (var (path, what) in new[] { (@base, "--base"), (overlay, "--overlay"), (rules, "--rules") })
            if (path == default || !path.FileExists())
            {
                _logger.LogError("{Option} {Path} does not exist", what, path);
                return (int)ExitCode.BadArguments;
            }

        if (output == default)
        {
            _logger.LogError("Set --output to the Wabbajack file to write");
            return (int)ExitCode.BadArguments;
        }

        try
        {
            var mergeRules = MergeRules.Parse(await rules.ReadAllTextAsync());
            _logger.LogInformation("Loading {Base} and {Overlay}", @base.FileName, overlay.FileName);
            var basePackage = await ModlistPackage.Load(_dtos, @base);
            var overlayPackage = await ModlistPackage.Load(_dtos, overlay);

            var (merged, report) = await ModlistMerger.Merge(basePackage, overlayPackage, mergeRules);
            await merged.Save(_dtos, output);
            var reportPath = output.Parent.Combine(output.FileName + ".merge-report.md");
            await reportPath.WriteAllTextAsync(report.ToMarkdown());

            var mods = report.Added.Count(a => !a.Name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase));
            _logger.LogInformation(
                "Wrote {Output}: {Mods} overlay mods, {Archives} more archives ({Size:0.00} GB), {Conflicts} loose-file conflicts",
                output, mods, report.ArchivesAdded, report.AddedDownloadSize / 1e9, report.Conflicts.Count);
            foreach (var w in report.Warnings) _logger.LogWarning("{Warning}", w);
            _logger.LogInformation("Report: {Report}", reportPath);
            return (int)ExitCode.Success;
        }
        catch (MergeException e)
        {
            _logger.LogError("{Message}", e.Message);
            return (int)ExitCode.Error;
        }
    }
}
