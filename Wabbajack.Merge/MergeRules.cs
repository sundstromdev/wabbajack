using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wabbajack.Merge;

/// <summary>
///     What to take from the overlay list and how to fit it into the base list. The base list is never
///     reordered or trimmed: everything in it is kept as it is, and the overlay's chosen mods go in as one
///     block. Every name here is a Mod Organizer mod (or separator) folder name, compared ignoring case.
/// </summary>
public sealed class MergeRules
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Base profiles to add the overlay block to. Empty means every profile in the base list.</summary>
    public List<string> BaseProfiles { get; set; } = new();

    /// <summary>
    ///     The overlay profile whose order and enabled/disabled state are copied. Required: an overlay with
    ///     several profiles (VRAM tiers, say) means different things in each.
    /// </summary>
    public string OverlayProfile { get; set; } = "";

    public IncludeRules Include { get; set; } = new();

    /// <summary>Overlay mods never to take, even when a selected separator contains them.</summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>
    ///     Skip an overlay mod whose files all come from Nexus mods the base list already has, under whatever
    ///     folder name. A mod listed by name in <see cref="IncludeRules.Mods" /> is taken anyway.
    /// </summary>
    public bool SkipDuplicatesOfBase { get; set; } = true;

    /// <summary>
    ///     The base mod or separator the overlay block goes directly above in priority terms: the block is
    ///     placed just before it in Mod Organizer's list, so it and everything after it win loose-file
    ///     conflicts against the block. Set this or <see cref="InsertAfter" />.
    /// </summary>
    public string? InsertBefore { get; set; }

    /// <summary>The base mod or separator the overlay block goes directly after.</summary>
    public string? InsertAfter { get; set; }

    /// <summary>Prepended to each overlay separator that is carried over, so the two lists stay distinguishable.</summary>
    public string SeparatorPrefix { get; set; } = "";

    /// <summary>
    ///     Overlay separator name to the name it gets in the merge. A null value drops that separator's header;
    ///     its mods then sit under whichever header came before.
    /// </summary>
    public Dictionary<string, string?> SeparatorRenames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Overlay mod to the base mod or separator it goes directly after, outside the block: for a mod made to
    ///     override a base mod's files (WEAVE replaces Equipment-EX's script), which has to sit above it in
    ///     priority. Keyed by the overlay mod's original name.
    /// </summary>
    public Dictionary<string, string> PlaceAfter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overlay mod name to the folder name it gets in the merge.</summary>
    public Dictionary<string, string> ModRenames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per overlay mod: which of its files to take. Keyed by the overlay mod's original name.</summary>
    public Dictionary<string, FileFilter> FileFilters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings sections to copy from an overlay ini into base ini files that lack them.</summary>
    public List<IniSectionAppend> IniSectionAppends { get; set; } = new();

    /// <summary>
    ///     Cyberpunk 2077 only. Adds a mod holding <c>archive\pc\mod\modlist.txt</c> that names the overlay's
    ///     archives in the overlay's order. The game loads the archives named there first, in that order, and
    ///     every other archive alphabetically after them, so the base list's own archives keep exactly the
    ///     order they had.
    /// </summary>
    public CyberpunkArchiveLoadOrder? CyberpunkArchiveLoadOrder { get; set; }

    public MergeMetadata Metadata { get; set; } = new();

    public static MergeRules Parse(string json)
    {
        MergeRules? rules;
        try
        {
            rules = JsonSerializer.Deserialize<MergeRules>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new MergeException($"The rules file is not valid JSON: {e.Message}");
        }

        return (rules ?? throw new MergeException("The rules file is empty")).Normalised();
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>System.Text.Json builds dictionaries with the default comparer; names compare ignoring case.</summary>
    private MergeRules Normalised()
    {
        SeparatorRenames = new Dictionary<string, string?>(SeparatorRenames ?? new(), StringComparer.OrdinalIgnoreCase);
        ModRenames = new Dictionary<string, string>(ModRenames ?? new(), StringComparer.OrdinalIgnoreCase);
        PlaceAfter = new Dictionary<string, string>(PlaceAfter ?? new(), StringComparer.OrdinalIgnoreCase);
        FileFilters = new Dictionary<string, FileFilter>(FileFilters ?? new(), StringComparer.OrdinalIgnoreCase);
        BaseProfiles ??= new List<string>();
        Exclude ??= new List<string>();
        Include ??= new IncludeRules();
        Include.Mods ??= new List<string>();
        Include.Separators ??= new List<string>();
        IniSectionAppends ??= new List<IniSectionAppend>();
        Metadata ??= new MergeMetadata();
        SeparatorPrefix ??= "";
        return this;
    }
}

public sealed class IncludeRules
{
    /// <summary>Overlay mods to take by name.</summary>
    public List<string> Mods { get; set; } = new();

    /// <summary>Overlay separators whose mods are all taken (still subject to exclude and duplicate checks).</summary>
    public List<string> Separators { get; set; } = new();
}

/// <summary>
///     Paths inside the mod folder, as prefixes compared ignoring case (<c>bin\x64\plugins\</c>). With
///     <see cref="Keep" /> set only matching files are taken; <see cref="Drop" /> then removes from that.
///     The mod's <c>meta.ini</c> is always taken.
/// </summary>
public sealed class FileFilter
{
    public List<string> Keep { get; set; } = new();
    public List<string> Drop { get; set; } = new();
}

public sealed class IniSectionAppend
{
    /// <summary>The ini in the overlay list, as installed: <c>mods\Some Mod\red4ext\plugins\x\user.ini</c>.</summary>
    public string From { get; set; } = "";

    /// <summary>The inis in the base list to add sections to. Each must be an inlined file.</summary>
    public List<string> To { get; set; } = new();

    /// <summary>Section names to copy. Empty copies every section the target does not already have.</summary>
    public List<string> Sections { get; set; } = new();
}

public sealed class CyberpunkArchiveLoadOrder
{
    public string ModName { get; set; } = "Merged Archive Load Order";
}

public sealed class MergeMetadata
{
    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
}
