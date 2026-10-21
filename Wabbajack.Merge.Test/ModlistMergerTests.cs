using Microsoft.Extensions.DependencyInjection;
using Wabbajack.DTOs;
using Wabbajack.DTOs.Directives;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Xunit;

namespace Wabbajack.Merge.Test;

public class ModlistMergerTests
{
    private const string BaseProfile = "Gameplay";
    private const string OverlayProfile = "2. Quality";

    /// <summary>
    ///     Base: frameworks, then gameplay. Overlay: the same frameworks, visuals, a gameplay mod and a
    ///     differently named copy of a base mod, in two profiles.
    /// </summary>
    private static (ModlistPackage Base, ModlistPackage Overlay) Lists()
    {
        var b = new ListBuilder("THING")
            .Separator("Utility_separator")
            .Mod("Cyber Engine Tweaks", 107, @"bin\x64\version.dll")
            .Mod("ArchiveXL", 4198, @"red4ext\plugins\ArchiveXL\ArchiveXL.dll")
            .Separator("Gameplay_separator")
            .Mod("Much Better AI", 500, @"r6\scripts\ai.reds")
            .Mod("Map Exit Lighting and HDR Fixes", 23420, @"archive\pc\mod\mapfix.archive")
            .Mod("No Paper Bags", 600, @"bin\x64\plugins\cyber_engine_tweaks\mods\NoPaperBags\init.lua")
            .Inline(@"mods\Gameplay Config\red4ext\plugins\mod_settings\user.ini", "[MuchBetterAI]\r\nenabled = true\r\n")
            .Inline(@"mods\Gameplay Config\meta.ini", "[General]\r\n")
            .Profile(BaseProfile, "+Utility_separator", "+Cyber Engine Tweaks", "+ArchiveXL", "+Gameplay_separator",
                "+Much Better AI", "+Map Exit Lighting and HDR Fixes", "+No Paper Bags", "+Gameplay Config")
            .Profile("Lite", "+Utility_separator", "+Cyber Engine Tweaks", "+ArchiveXL", "+Gameplay_separator",
                "+Much Better AI", "-Map Exit Lighting and HDR Fixes", "+No Paper Bags", "+Gameplay Config")
            .Build();

        var o = new ListBuilder("Ultrapunk")
            .Separator("Core_separator")
            .Mod("Cyber Engine Tweaks", 107, @"bin\x64\version.dll", @"bin\x64\extra.dll")
            .Mod("Ultra Plus", 10490, @"bin\x64\plugins\cyber_engine_tweaks\mods\UltraPlus\init.lua",
                @"archive\pc\mod\UltraPlusH10.archive")
            .Separator("Core Visual_separator")
            .Mod("Nova City", 12490, @"archive\pc\mod\#NovaCity.archive", @"archive\pc\mod\NovaCity.xl")
            .Mod("Blur Begone", 13000, @"archive\pc\mod\BlurBegone.archive", @"archive\pc\mod\#A.archive")
            .Mod("ETO 2K", 14000, @"archive\pc\mod\eto.archive")
            .Mod("ETO 4K", 14000, @"archive\pc\mod\eto.archive")
            .Mod("Classic Drinks", 15000, @"bin\x64\plugins\cyber_engine_tweaks\mods\NoPaperBags\init.lua")
            .Separator("Fixes_separator")
            .Mod("Map Exit Nuclear Explosion Fix", 23420, @"archive\pc\mod\mapfix.archive")
            .Mod("Visible Bullets", 22251, @"r6\tweaks\bullets.yaml")
            .Mod("Decals Flickering Fix", 16000, @"archive\pc\mod\decals.archive")
            .Inline(@"mods\Config Files\red4ext\plugins\mod_settings\user.ini",
                "[MuchBetterAI]\r\nenabled = false\r\n\r\n[NovaCity]\r\nexposure = 1\r\n\r\n[Bullets]\r\nfast = true\r\n")
            .Inline(@"mods\Config Files\bin\x64\plugins\cyber_engine_tweaks\mods\UltraPlus\config.ini", "[UltraPlus]\r\nmode=PT\r\n")
            .Inline(@"mods\Config Files\bin\x64\plugins\cyber_engine_tweaks\mods\Bullets\settings.json", "{}")
            .Inline(@"mods\Config Files\meta.ini", "[General]\r\n")
            .Profile(OverlayProfile, "+Core_separator", "+Cyber Engine Tweaks", "+Ultra Plus", "+Core Visual_separator",
                "+Nova City", "+Blur Begone", "+ETO 2K", "-ETO 4K", "+Classic Drinks", "+Fixes_separator",
                "+Map Exit Nuclear Explosion Fix", "+Visible Bullets", "+Decals Flickering Fix", "+Config Files")
            .Profile("1. Performance", "+Core_separator", "+Cyber Engine Tweaks", "+Ultra Plus", "+Core Visual_separator",
                "+Nova City", "-Blur Begone", "+ETO 2K", "-ETO 4K", "+Classic Drinks", "+Fixes_separator",
                "+Map Exit Nuclear Explosion Fix", "+Visible Bullets", "+Decals Flickering Fix", "+Config Files")
            .Build();
        return (b, o);
    }

    private static MergeRules Rules() => new()
    {
        OverlayProfile = OverlayProfile,
        InsertBefore = "Gameplay_separator",
        SeparatorPrefix = "UP ",
        Include = new IncludeRules
        {
            Mods = { "Ultra Plus", "Decals Flickering Fix", "Config Files" },
            Separators = { "Core Visual_separator" }
        }
    };

    [Fact]
    public async Task BlockGoesNextToTheAnchorAndBaseOrderIsUntouched()
    {
        var (b, o) = Lists();
        var (merged, _) = await ModlistMerger.Merge(b, o, Rules());

        Assert.Equal(new[]
        {
            "+Utility_separator", "+Cyber Engine Tweaks", "+ArchiveXL",
            "+UP Core_separator", "+Ultra Plus",
            "+UP Core Visual_separator", "+Nova City", "+Blur Begone", "+ETO 2K", "-ETO 4K", "+Classic Drinks",
            "+UP Fixes_separator", "+Decals Flickering Fix", "+Config Files",
            "+Gameplay_separator", "+Much Better AI", "+Map Exit Lighting and HDR Fixes", "+No Paper Bags",
            "+Gameplay Config"
        }, merged.ProfileLines(BaseProfile));

        // The other base profile gets the same block and keeps its own disabled mod.
        var lite = merged.ProfileLines("Lite");
        Assert.Contains("-Map Exit Lighting and HDR Fixes", lite);
        Assert.Contains("+Nova City", lite);
    }

    [Fact]
    public async Task InsertAfterPutsTheBlockBelowTheAnchor()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.InsertBefore = null;
        rules.InsertAfter = "Gameplay Config";
        var (merged, _) = await ModlistMerger.Merge(b, o, rules);
        var lines = merged.ProfileLines(BaseProfile);
        Assert.Equal("+Gameplay Config", lines[7]);
        Assert.Equal("+UP Core_separator", lines[8]);
    }

    [Fact]
    public async Task EveryBaseDirectiveSurvivesExceptTheRewrittenProfiles()
    {
        var (b, o) = Lists();
        var (merged, _) = await ModlistMerger.Merge(b, o, Rules());
        var after = merged.ModList.Directives.ToDictionary(d => d.To);
        foreach (var d in b.ModList.Directives)
        {
            if (d.To.Parts[0] == "profiles") continue;
            Assert.Same(d, after[d.To]);
        }

        Assert.All(b.ModList.Archives, a => Assert.Contains(a, merged.ModList.Archives));
    }

    [Fact]
    public async Task AModBothListsHaveKeepsTheBaseCopy()
    {
        var (b, o) = Lists();
        var (merged, report) = await ModlistMerger.Merge(b, o, Rules().AlsoInclude("Cyber Engine Tweaks"));
        Assert.False(merged.Installs(@"mods\Cyber Engine Tweaks\bin\x64\extra.dll"));
        Assert.Same(b.At(@"mods\Cyber Engine Tweaks\bin\x64\version.dll"), merged.At(@"mods\Cyber Engine Tweaks\bin\x64\version.dll"));
        Assert.Contains(report.Skipped, s => s.Name == "Cyber Engine Tweaks" && s.Reason == SkipReason.SharedWithBase);
    }

    [Fact]
    public async Task ADuplicateUnderAnotherNameIsSkippedUnlessNamed()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.Include.Separators.Add("Fixes_separator");
        rules.Exclude.Add("Visible Bullets");
        var (merged, report) = await ModlistMerger.Merge(b, o, rules);
        Assert.False(merged.Installs(@"mods\Map Exit Nuclear Explosion Fix\archive\pc\mod\mapfix.archive"));
        var skipped = Assert.Single(report.Skipped, s => s.Name == "Map Exit Nuclear Explosion Fix");
        Assert.Equal(SkipReason.DuplicateOfBase, skipped.Reason);
        Assert.Contains("Map Exit Lighting and HDR Fixes", skipped.Detail);
        Assert.Contains(report.Skipped, s => s.Name == "Visible Bullets" && s.Reason == SkipReason.Excluded);

        rules.Include.Mods.Add("Map Exit Nuclear Explosion Fix");
        (merged, report) = await ModlistMerger.Merge(b, o, rules);
        Assert.True(merged.Installs(@"mods\Map Exit Nuclear Explosion Fix\archive\pc\mod\mapfix.archive"));
        Assert.Contains(report.Warnings, w => w.Contains("Map Exit Nuclear Explosion Fix"));
    }

    [Fact]
    public async Task NotSelectedModsAreReportedAndLeftOut()
    {
        var (b, o) = Lists();
        var (merged, report) = await ModlistMerger.Merge(b, o, Rules());
        Assert.False(merged.Installs(@"mods\Visible Bullets\r6\tweaks\bullets.yaml"));
        Assert.Contains(report.Skipped, s => s.Name == "Visible Bullets" && s.Reason == SkipReason.NotSelected);
    }

    [Fact]
    public async Task FileFiltersKeepOnlyTheNamedPathsAndMetaIni()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.FileFilters["Config Files"] = new FileFilter { Keep = { @"bin\x64\plugins\cyber_engine_tweaks\mods\UltraPlus\" } };
        rules.ModRenames["Config Files"] = "UP Config";
        var (merged, _) = await ModlistMerger.Merge(b, o, rules);
        Assert.True(merged.Installs(@"mods\UP Config\bin\x64\plugins\cyber_engine_tweaks\mods\UltraPlus\config.ini"));
        Assert.True(merged.Installs(@"mods\UP Config\meta.ini"));
        Assert.False(merged.Installs(@"mods\UP Config\bin\x64\plugins\cyber_engine_tweaks\mods\Bullets\settings.json"));
        Assert.False(merged.Installs(@"mods\UP Config\red4ext\plugins\mod_settings\user.ini"));
        Assert.Equal("[UltraPlus]\r\nmode=PT\r\n", merged.Text(@"mods\UP Config\bin\x64\plugins\cyber_engine_tweaks\mods\UltraPlus\config.ini"));
        Assert.Contains("+UP Config", merged.ProfileLines(BaseProfile));
    }

    [Fact]
    public async Task IniSectionsAreAddedOnlyWhereMissing()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.IniSectionAppends.Add(new IniSectionAppend
        {
            From = @"mods\Config Files\red4ext\plugins\mod_settings\user.ini",
            To = { @"mods\Gameplay Config\red4ext\plugins\mod_settings\user.ini" }
        });
        var (merged, report) = await ModlistMerger.Merge(b, o, rules);
        var text = merged.Text(@"mods\Gameplay Config\red4ext\plugins\mod_settings\user.ini");
        Assert.StartsWith("[MuchBetterAI]\r\nenabled = true\r\n", text);
        Assert.Contains("[NovaCity]\r\nexposure = 1", text);
        Assert.DoesNotContain("enabled = false", text);
        Assert.Single(report.IniSectionsAdded);

        rules.IniSectionAppends[0].Sections.Add("NovaCity");
        (merged, _) = await ModlistMerger.Merge(b, o, rules);
        text = merged.Text(@"mods\Gameplay Config\red4ext\plugins\mod_settings\user.ini");
        Assert.Contains("[NovaCity]", text);
        Assert.DoesNotContain("[Bullets]", text);
    }

    [Fact]
    public async Task CyberpunkLoadOrderNamesEnabledOverlayArchivesInOverlayOrder()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.CyberpunkArchiveLoadOrder = new CyberpunkArchiveLoadOrder { ModName = "UP Archive Load Order" };
        var (merged, report) = await ModlistMerger.Merge(b, o, rules);

        var order = merged.Text(@"mods\UP Archive Load Order\archive\pc\mod\modlist.txt").Split('\n');
        Assert.Equal(new[]
        {
            "UltraPlusH10.archive", "#NovaCity.archive", "#A.archive", "BlurBegone.archive", "eto.archive",
            "decals.archive"
        }, order);
        Assert.Equal(order, report.ArchiveLoadOrder);
        Assert.DoesNotContain("mapfix.archive", order);
        Assert.Equal("+UP Archive Load Order", merged.ProfileLines(BaseProfile)[14]);
    }

    [Fact]
    public async Task SeparatorRenamesCanDropAHeader()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.SeparatorRenames["Core_separator"] = null;
        rules.SeparatorRenames["Core Visual_separator"] = "Visuals_separator";
        var (merged, _) = await ModlistMerger.Merge(b, o, rules);
        var lines = merged.ProfileLines(BaseProfile);
        Assert.DoesNotContain("+UP Core_separator", lines);
        Assert.Contains("+Visuals_separator", lines);
        Assert.True(merged.Installs(@"mods\Visuals_separator\meta.ini"));
        Assert.Equal("+Ultra Plus", lines[3]);
    }

    [Fact]
    public async Task ClashingSeparatorNamesAreRefused()
    {
        var b = new ListBuilder("B").Separator("Visual_separator").Mod("A", 1, "a.txt")
            .Profile("P", "+Visual_separator", "+A").Build();
        var o = new ListBuilder("O").Separator("Visual_separator").Mod("V", 2, "v.txt")
            .Profile("Q", "+Visual_separator", "+V").Build();
        var rules = new MergeRules
        {
            OverlayProfile = "Q", InsertAfter = "A", Include = new IncludeRules { Separators = { "Visual_separator" } }
        };
        var e = await Assert.ThrowsAsync<MergeException>(() => ModlistMerger.Merge(b, o, rules));
        Assert.Contains("separatorPrefix", e.Message);

        rules.SeparatorPrefix = "O ";
        var (merged, _) = await ModlistMerger.Merge(b, o, rules);
        Assert.Equal(new[] { "+Visual_separator", "+A", "+O Visual_separator", "+V" }, merged.ProfileLines("P"));
    }

    [Fact]
    public async Task UnknownNamesInTheRulesAreErrors()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.Include.Mods.Add("Nova Cty");
        rules.Exclude.Add("Nope");
        var e = await Assert.ThrowsAsync<MergeException>(() => ModlistMerger.Merge(b, o, rules));
        Assert.Contains("Nova Cty", e.Message);
        Assert.Contains("Nope", e.Message);
    }

    [Fact]
    public async Task AMissingAnchorOrProfileIsAnError()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.InsertBefore = "Not There";
        await Assert.ThrowsAsync<MergeException>(() => ModlistMerger.Merge(b, o, rules));

        rules = Rules();
        rules.OverlayProfile = "3. Ultra";
        var e = await Assert.ThrowsAsync<MergeException>(() => ModlistMerger.Merge(b, o, rules));
        Assert.Contains("1. Performance", e.Message);

        rules = Rules();
        rules.InsertAfter = "ArchiveXL";
        await Assert.ThrowsAsync<MergeException>(() => ModlistMerger.Merge(b, o, rules));
    }

    [Fact]
    public async Task ListsWithPluginLoadOrdersAreRefused()
    {
        var b = new ListBuilder("Skyrim list").ForGame(Game.SkyrimSpecialEdition).Mod("A", 1, "a.esp")
            .Profile("P", "+A").Inline(@"profiles\P\plugins.txt", "*a.esp").Build();
        var o = new ListBuilder("Other").ForGame(Game.SkyrimSpecialEdition).Mod("B", 2, "b.esp")
            .Profile("Q", "+B").Build();
        var rules = new MergeRules { OverlayProfile = "Q", InsertAfter = "A", Include = new IncludeRules { Mods = { "B" } } };
        var e = await Assert.ThrowsAsync<MergeException>(() => ModlistMerger.Merge(b, o, rules));
        Assert.Contains("plugin load order", e.Message);
    }

    [Fact]
    public async Task ArchivesWithTheSameNameButDifferentContentAreRefused()
    {
        var bb = new ListBuilder("B");
        var shared = bb.NexusArchive("Shared.7z", 1);
        var b = bb.ModFrom("A", shared, "a.txt").Profile("P", "+A").Build();
        var ob = new ListBuilder("O");
        var other = ob.NexusArchive("Shared.7z", 2);
        var o = ob.ModFrom("V", other, "v.txt").Profile("Q", "+V").Build();
        var rules = new MergeRules { OverlayProfile = "Q", InsertAfter = "A", Include = new IncludeRules { Mods = { "V" } } };
        var e = await Assert.ThrowsAsync<MergeException>(() => ModlistMerger.Merge(b, o, rules));
        Assert.Contains("Shared.7z", e.Message);
    }

    [Fact]
    public async Task OverlayArchivesAreAddedOnceAndOnlyWhenUsed()
    {
        var (b, o) = Lists();
        var (merged, report) = await ModlistMerger.Merge(b, o, Rules());
        var used = merged.ModList.Directives.OfType<FromArchive>().Select(d => d.ArchiveHashPath.Hash).ToHashSet();
        Assert.All(merged.ModList.Archives.Except(b.ModList.Archives), a => Assert.Contains(a.Hash, used));
        Assert.DoesNotContain(merged.ModList.Archives, a => a.Name == "Visible Bullets.zip");
        Assert.Equal(merged.ModList.Archives.Length - b.ModList.Archives.Length, report.ArchivesAdded);
    }

    [Fact]
    public async Task LooseFileConflictsAreReportedWithTheWinner()
    {
        var (b, o) = Lists();
        var (_, report) = await ModlistMerger.Merge(b, o, Rules());
        Assert.Equal(2, report.Conflicts.Count);
        var c = report.Conflicts.Single(x => x.Path == @"bin\x64\plugins\cyber_engine_tweaks\mods\NoPaperBags\init.lua");
        Assert.Equal("No Paper Bags", c.Winner);
        Assert.Equal(new[] { "Classic Drinks" }, c.Losers);

        // Both lists ship a Mod Settings user.ini; the base's config mod sits above the block, so it wins.
        var ini = report.Conflicts.Single(x => x.Path == @"red4ext\plugins\mod_settings\user.ini");
        Assert.Equal("Gameplay Config", ini.Winner);
        Assert.Equal(new[] { "Config Files" }, ini.Losers);
    }

    [Fact]
    public async Task InlinedFilesWrittenByTheMergeHaveCorrectHashes()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.CyberpunkArchiveLoadOrder = new CyberpunkArchiveLoadOrder();
        var (merged, _) = await ModlistMerger.Merge(b, o, rules);
        foreach (var d in merged.ModList.Directives.OfType<InlineFile>())
        {
            var data = merged.Data(d.SourceDataID);
            Assert.Equal(d.Size, data.Length);
            Assert.Equal(d.Hash, await data.Hash());
        }
    }

    [Fact]
    public async Task MetadataComesFromTheRules()
    {
        var (b, o) = Lists();
        var rules = Rules();
        rules.Metadata = new MergeMetadata { Name = "THING x Ultrapunk", Version = "1.2.0" };
        var (merged, _) = await ModlistMerger.Merge(b, o, rules);
        Assert.Equal("THING x Ultrapunk", merged.ModList.Name);
        Assert.Equal(new Version(1, 2, 0), merged.ModList.Version);
        Assert.Equal(Game.Cyberpunk2077, merged.ModList.GameType);
    }

    [Fact]
    public async Task AMergedFileSurvivesSavingAndLoading()
    {
        var (b, o) = Lists();
        var (merged, _) = await ModlistMerger.Merge(b, o, Rules());
        var dtos = new ServiceCollection().AddDTOConverters().AddDTOSerializer().BuildServiceProvider()
            .GetRequiredService<DTOSerializer>();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wabbajack").ToAbsolutePath();
        try
        {
            await merged.Save(dtos, path);
            var loaded = await ModlistPackage.Load(dtos, path);
            Assert.Equal(merged.ModList.Directives.Length, loaded.ModList.Directives.Length);
            Assert.Equal(merged.ProfileLines(BaseProfile), loaded.ProfileLines(BaseProfile));
            Assert.Equal(merged.Entries.Keys.OrderBy(k => k), loaded.Entries.Keys.OrderBy(k => k));
            Assert.Empty(await PackageValidator.Validate(loaded, loaded.ModList.Directives));
        }
        finally
        {
            path.Delete();
        }
    }

    [Fact]
    public void RulesParseFromJsonWithCommentsAndAnyCase()
    {
        var rules = MergeRules.Parse("""
            {
              // comments are allowed
              "overlayProfile": "2. Quality",
              "insertBefore": "Gameplay_separator",
              "include": { "mods": ["Ultra Plus"], "separators": ["Core Visual_separator"] },
              "modRenames": { "Config Files": "UP Config" },
              "separatorRenames": { "Core_separator": null },
              "cyberpunkArchiveLoadOrder": { "modName": "UP LO" },
            }
            """);
        Assert.Equal("UP Config", rules.ModRenames["config files"]);
        Assert.True(rules.SeparatorRenames.ContainsKey("CORE_SEPARATOR"));
        Assert.Null(rules.SeparatorRenames["Core_separator"]);
        Assert.Equal("UP LO", rules.CyberpunkArchiveLoadOrder!.ModName);
        Assert.True(rules.SkipDuplicatesOfBase);
        Assert.Throws<MergeException>(() => MergeRules.Parse("{ nope"));
    }
}

internal static class RulesExtensions
{
    public static MergeRules AlsoInclude(this MergeRules rules, string mod)
    {
        rules.Include.Mods.Add(mod);
        return rules;
    }
}
