using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Merge;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.CLI.Verbs;

public class PlanMerge
{
    private readonly ILogger<PlanMerge> _logger;
    private readonly DTOSerializer _dtos;

    public PlanMerge(ILogger<PlanMerge> logger, DTOSerializer dtos)
    {
        _logger = logger;
        _dtos = dtos;
    }

    public static VerbDefinition Definition = new("plan-merge",
        "Drafts merge rules for merge-modlists from two Wabbajack files, with a markdown list of every overlay mod " +
        "marked new, shared with the base or a duplicate of a base mod.",
        [
            new OptionDefinition(typeof(AbsolutePath), "b", "base", "Base Wabbajack file"),
            new OptionDefinition(typeof(AbsolutePath), "ol", "overlay", "Overlay Wabbajack file"),
            new OptionDefinition(typeof(string), "p", "profile", "Overlay profile to use (default: the last one alphabetically)"),
            new OptionDefinition(typeof(AbsolutePath), "o", "output", "Rules file to write; the plan is written beside it as .md")
        ]);

    internal async Task<int> Run(AbsolutePath @base, AbsolutePath overlay, string profile, AbsolutePath output)
    {
        foreach (var (path, what) in new[] { (@base, "--base"), (overlay, "--overlay") })
            if (path == default || !path.FileExists())
            {
                _logger.LogError("{Option} {Path} does not exist", what, path);
                return (int)ExitCode.BadArguments;
            }

        if (output == default)
        {
            _logger.LogError("Set --output to the rules file to write");
            return (int)ExitCode.BadArguments;
        }

        try
        {
            var basePackage = await ModlistPackage.Load(_dtos, @base);
            var overlayPackage = await ModlistPackage.Load(_dtos, overlay);
            var (rules, plan) = MergePlanner.Draft(basePackage, overlayPackage, profile);
            output.Parent.CreateDirectory();
            await output.WriteAllTextAsync(rules.ToJson());
            var planPath = output.Parent.Combine(output.FileName.WithoutExtension() + ".plan.md");
            await planPath.WriteAllTextAsync(plan);
            _logger.LogInformation("Wrote {Rules} ({Mods} overlay mods drafted in) and {Plan}", output,
                rules.Include.Mods.Count, planPath);
            return (int)ExitCode.Success;
        }
        catch (MergeException e)
        {
            _logger.LogError("{Message}", e.Message);
            return (int)ExitCode.Error;
        }
    }
}
