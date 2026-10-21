using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Wabbajack.App.Avalonia.Services;
using Wabbajack.Common;
using Wabbajack.DTOs;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Merge;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.App.Avalonia.ViewModels.Merge;

public sealed record AnchorItem(string Name, bool IsSeparator)
{
    public string Display => IsSeparator ? "▸ " + Name[..^"_separator".Length] : "    " + Name;
}

/// <summary>
///     Merge lists: pick a base Wabbajack file (kept as it is) and an overlay to take mods from, tick the overlay
///     mods to bring in, choose where in the base list they go, and write a new Wabbajack file. The same rules
///     files the CLI's merge-modlists reads can be loaded and saved here, so a merge set up once can be redone
///     against updated lists.
/// </summary>
public partial class MergeVM : ViewModel
{
    private readonly ILogger<MergeVM> _logger;
    private readonly DTOSerializer _dtos;
    private readonly FilePicker _picker;
    private ModlistPackage? _base;
    private ModlistPackage? _overlay;
    private MergeRules? _template;
    private List<MergeModRowVM> _allRows = new();

    public MergeVM(ILogger<MergeVM> logger, DTOSerializer dtos, FilePicker picker, MergeListPicker galleryPicker,
        Navigator navigator)
    {
        _logger = logger;
        _dtos = dtos;
        _picker = picker;

        PickBaseCommand = ReactiveCommand.CreateFromTask(() => PickList(true));
        PickOverlayCommand = ReactiveCommand.CreateFromTask(() => PickList(false));
        // Browse borrows the gallery itself; its details pane hands the chosen list back through the picker.
        BrowseBaseCommand = ReactiveCommand.Create(() =>
        {
            galleryPicker.Begin(true);
            navigator.NavigateTo(ScreenType.ModListGallery);
        });
        BrowseOverlayCommand = ReactiveCommand.Create(() =>
        {
            galleryPicker.Begin(false);
            navigator.NavigateTo(ScreenType.ModListGallery);
        });
        galleryPicker.Picked
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(p => LoadList(p.ForBase, p.Path).FireAndForget())
            .DisposeWith(CompositeDisposable);
        PickOutputCommand = ReactiveCommand.CreateFromTask(PickOutput);
        SelectAllNewCommand = ReactiveCommand.Create(() => SetAll(r => r.IsNew));
        SelectNoneCommand = ReactiveCommand.Create(() => SetAll(_ => false));
        LoadRulesCommand = ReactiveCommand.CreateFromTask(LoadRules);
        SaveRulesCommand = ReactiveCommand.CreateFromTask(SaveRules);
        MergeCommand = ReactiveCommand.CreateFromTask(RunMerge,
            this.WhenAnyValue(x => x.IsReady, x => x.IsBusy, (ready, busy) => ready && !busy));

        this.WhenAnyValue(x => x.SelectedOverlayProfile)
            .Subscribe(_ => RebuildRows())
            .DisposeWith(CompositeDisposable);
        this.WhenAnyValue(x => x.Filter, x => x.OnlyNew)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(CompositeDisposable);
        this.WhenAnyValue(x => x.SelectedAnchor, x => x.SelectedOverlayProfile, x => x.OutputPath)
            .Subscribe(_ => UpdateReady())
            .DisposeWith(CompositeDisposable);
    }

    public ICommand PickBaseCommand { get; }
    public ICommand BrowseBaseCommand { get; }
    public ICommand BrowseOverlayCommand { get; }
    public ICommand PickOverlayCommand { get; }
    public ICommand PickOutputCommand { get; }
    public ICommand SelectAllNewCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand LoadRulesCommand { get; }
    public ICommand SaveRulesCommand { get; }
    public ICommand MergeCommand { get; }

    [Reactive] public partial string BaseTitle { get; set; } = "Choose the list whose gameplay, settings and order you keep";
    [Reactive] public partial string OverlayTitle { get; set; } = "Choose the list to take mods from";
    [Reactive] public partial ObservableCollection<string> OverlayProfiles { get; set; } = new();
    [Reactive] public partial string? SelectedOverlayProfile { get; set; }
    [Reactive] public partial ObservableCollection<AnchorItem> Anchors { get; set; } = new();
    [Reactive] public partial AnchorItem? SelectedAnchor { get; set; }
    public IReadOnlyList<string> Placements { get; } = new[] { "Above (lower priority than) this entry", "Below (higher priority than) this entry" };
    [Reactive] public partial int PlacementIndex { get; set; }
    [Reactive] public partial string SeparatorPrefix { get; set; } = "";
    [Reactive] public partial bool IsCyberpunk { get; set; }
    [Reactive] public partial bool ArchiveLoadOrder { get; set; } = true;
    [Reactive] public partial AbsolutePath OutputPath { get; set; }
    [Reactive] public partial string OutputText { get; set; } = "Choose where to write the merged Wabbajack file";
    [Reactive] public partial string Filter { get; set; } = "";
    [Reactive] public partial bool OnlyNew { get; set; }
    [Reactive] public partial ObservableCollection<MergeModRowVM> Rows { get; set; } = new();
    [Reactive] public partial string SelectionSummary { get; set; } = "";
    [Reactive] public partial string Status { get; set; } = "";
    [Reactive] public partial string Report { get; set; } = "";
    [Reactive] public partial bool IsBusy { get; set; }
    [Reactive] public partial bool IsReady { get; set; }

    private async Task PickList(bool isBase)
    {
        var path = await _picker.PickFile(isBase ? "Choose the base Wabbajack file" : "Choose the overlay Wabbajack file",
            ("Wabbajack file", "*.wabbajack"));
        if (path == default) return;
        await LoadList(isBase, path);
    }

    private async Task LoadList(bool isBase, AbsolutePath path)
    {
        IsBusy = true;
        Status = $"Reading {path.FileName}…";
        try
        {
            var package = await Task.Run(() => ModlistPackage.Load(_dtos, path));
            var profiles = MergePlanner.Profiles(package);
            if (profiles.Count == 0)
                throw new MergeException($"{package.ModList.Name} is not a Mod Organizer list, so it cannot be merged");
            var title = $"{package.ModList.Name} {package.ModList.Version} — {package.ModList.GameType.MetaData().HumanFriendlyGameName}, {path.FileName}";

            if (isBase)
            {
                _base = package;
                BaseTitle = title;
                IsCyberpunk = package.ModList.GameType == Game.Cyberpunk2077;
                var anchors = MergePlanner.BaseEntries(package, profiles.First()).Select(e => new AnchorItem(e.Name, e.IsSeparator));
                Anchors = new ObservableCollection<AnchorItem>(anchors);
                SelectedAnchor = PreferredAnchor();
            }
            else
            {
                _overlay = package;
                OverlayTitle = title;
                if (string.IsNullOrEmpty(SeparatorPrefix)) SeparatorPrefix = Initials(package.ModList.Name) + " ";
                OverlayProfiles = new ObservableCollection<string>(profiles);
                SelectedOverlayProfile = _template?.OverlayProfile is { } p && profiles.Contains(p) ? p : profiles.Last();
            }

            if (OutputPath == default && _base != null && _overlay != null)
            {
                // Lists from the gallery live in the app's own cache, which is no place for a result.
                var folder = KnownFolders.Downloads.DirectoryExists() ? KnownFolders.Downloads : path.Parent;
                var name = $"{_base.ModList.Name} + {_overlay.ModList.Name}.wabbajack";
                foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(bad, '-');
                OutputPath = folder.Combine(name);
                OutputText = OutputPath.ToString();
            }

            RebuildRows();
            Status = "";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not read {Path}", path);
            Status = e is MergeException ? e.Message : $"Could not read {path.FileName}: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PickOutput()
    {
        var path = await _picker.PickSaveFile("Write the merged Wabbajack file", OutputPath,
            OutputPath == default ? "Merged.wabbajack" : OutputPath.FileName.ToString(), ("Wabbajack file", "*.wabbajack"));
        if (path == default) return;
        OutputPath = path;
        OutputText = path.ToString();
    }

    private void RebuildRows()
    {
        var keep = _allRows.Where(r => !r.IsHeader && r.IsChecked == true).Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_base == null || _overlay == null || SelectedOverlayProfile == null)
        {
            _allRows = new List<MergeModRowVM>();
            ApplyFilter();
            return;
        }

        _allRows = MergeModRowVM.Build(MergePlanner.Analyse(_base, _overlay, SelectedOverlayProfile));
        foreach (var row in _allRows)
        {
            if (row.IsHeader) continue;
            row.WhenAnyValue(r => r.IsChecked).Subscribe(_ => UpdateSummary()).DisposeWith(CompositeDisposable);
        }

        ApplyTemplateSelection(keep);
        ApplyFilter();
        UpdateSummary();
    }

    /// <summary>What was ticked before (or what a loaded rules file names) stays ticked when the rows are rebuilt.</summary>
    private void ApplyTemplateSelection(HashSet<string> keep)
    {
        var mods = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        var seps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_template != null && keep.Count == 0)
        {
            mods.UnionWith(_template.Include.Mods);
            seps.UnionWith(_template.Include.Separators);
            excluded.UnionWith(_template.Exclude);
        }

        foreach (var row in _allRows.Where(r => !r.IsHeader))
        {
            var byName = mods.Contains(row.Name);
            var bySep = row.Mod!.Separator != null && seps.Contains(row.Mod.Separator) && row.IsNew;
            if ((byName || bySep) && !excluded.Contains(row.Name)) row.IsChecked = true;
        }
    }

    private void ApplyFilter()
    {
        var filter = Filter?.Trim() ?? "";
        bool Visible(MergeModRowVM r) =>
            (!OnlyNew || r.IsNew) &&
            (filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
             (r.Header?.Display.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));

        var rows = new List<MergeModRowVM>();
        foreach (var header in _allRows.Where(r => r.IsHeader))
        {
            var children = header.Children.Where(Visible).ToList();
            if (children.Count == 0) continue;
            rows.Add(header);
            rows.AddRange(children);
        }

        Rows = new ObservableCollection<MergeModRowVM>(rows);
    }

    private void SetAll(Func<MergeModRowVM, bool> check)
    {
        foreach (var row in _allRows.Where(r => !r.IsHeader)) row.IsChecked = check(row);
    }

    private void UpdateSummary()
    {
        var mods = _allRows.Where(r => !r.IsHeader).ToList();
        SelectionSummary = mods.Count == 0
            ? ""
            : $"{mods.Count(r => r.IsChecked == true)} selected · {mods.Count(r => r.IsNew)} new · " +
              $"{mods.Count(r => r.Mod!.Status == OverlayModStatus.Shared)} already in base · " +
              $"{mods.Count(r => r.Mod!.Status == OverlayModStatus.Duplicate)} duplicates";
        UpdateReady();
    }

    private void UpdateReady() =>
        IsReady = _base != null && _overlay != null && SelectedOverlayProfile != null && SelectedAnchor != null &&
                  OutputPath != default && _allRows.Any(r => !r.IsHeader && r.IsChecked == true);

    private AnchorItem? PreferredAnchor()
    {
        var wanted = _template?.InsertBefore ?? _template?.InsertAfter;
        if (wanted != null && Anchors.FirstOrDefault(a => a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } a)
        {
            PlacementIndex = _template!.InsertBefore != null ? 0 : 1;
            return a;
        }

        return null;
    }

    /// <summary>
    ///     The rules the screen describes. A loaded rules file supplies everything the screen has no control for
    ///     (renames, file filters, ini sections, metadata); what the screen does show replaces its values.
    /// </summary>
    public MergeRules BuildRules()
    {
        var rules = _template != null ? MergeRules.Parse(_template.ToJson()) : new MergeRules();
        rules.OverlayProfile = SelectedOverlayProfile ?? "";
        rules.Include = new IncludeRules
        {
            Mods = _allRows.Where(r => !r.IsHeader && r.IsChecked == true).Select(r => r.Name).ToList()
        };
        rules.Exclude = new List<string>();
        rules.InsertBefore = PlacementIndex == 0 ? SelectedAnchor?.Name : null;
        rules.InsertAfter = PlacementIndex == 1 ? SelectedAnchor?.Name : null;
        rules.SeparatorPrefix = SeparatorPrefix ?? "";
        rules.CyberpunkArchiveLoadOrder = IsCyberpunk && ArchiveLoadOrder
            ? rules.CyberpunkArchiveLoadOrder ?? new CyberpunkArchiveLoadOrder
            {
                ModName = $"{(string.IsNullOrWhiteSpace(SeparatorPrefix) ? "Merged" : SeparatorPrefix.Trim())} Archive Load Order"
            }
            : null;
        if (_base != null && _overlay != null)
            rules.Metadata.Name ??= $"{_base.ModList.Name} + {_overlay.ModList.Name}";
        return rules;
    }

    private async Task LoadRules()
    {
        var path = await _picker.PickFile("Load merge rules", ("Merge rules", "*.json"));
        if (path == default) return;
        try
        {
            _template = MergeRules.Parse(await path.ReadAllTextAsync());
            SeparatorPrefix = _template.SeparatorPrefix;
            ArchiveLoadOrder = _template.CyberpunkArchiveLoadOrder != null;
            if (_template.OverlayProfile is { Length: > 0 } p && OverlayProfiles.Contains(p)) SelectedOverlayProfile = p;
            SelectedAnchor = PreferredAnchor() ?? SelectedAnchor;
            foreach (var row in _allRows.Where(r => !r.IsHeader)) row.IsChecked = false;
            ApplyTemplateSelection(new HashSet<string>());
            Status = $"Loaded {path.FileName}";
        }
        catch (Exception e)
        {
            _template = null;
            Status = e is MergeException ? e.Message : $"Could not read {path.FileName}: {e.Message}";
        }
    }

    private async Task SaveRules()
    {
        var path = await _picker.PickSaveFile("Save merge rules", default, "merge.rules.json", ("Merge rules", "*.json"));
        if (path == default) return;
        await path.WriteAllTextAsync(BuildRules().ToJson());
        Status = $"Saved {path.FileName}";
    }

    private async Task RunMerge()
    {
        if (_base == null || _overlay == null) return;
        IsBusy = true;
        Report = "";
        Status = "Merging…";
        try
        {
            var rules = BuildRules();
            var output = OutputPath;
            var (merged, report) = await Task.Run(() => ModlistMerger.Merge(_base, _overlay, rules));
            Status = $"Writing {output.FileName}…";
            await Task.Run(() => merged.Save(_dtos, output));
            var reportPath = output.Parent.Combine(output.FileName + ".merge-report.md");
            await reportPath.WriteAllTextAsync(report.ToMarkdown());
            Report = Summarise(report, output, reportPath);
            Status = $"Done: {output.FileName}. Install it with Install from disk.";
        }
        catch (MergeException e)
        {
            Status = "The merge could not go ahead.";
            Report = e.Message;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Merging modlists");
            Status = "The merge failed.";
            Report = e.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Summarise(MergeReport report, AbsolutePath output, AbsolutePath reportPath)
    {
        var sb = new StringBuilder();
        var mods = report.Added.Count(a => !a.Name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"{mods} mods added from {report.OverlayName} into {string.Join(" and ", report.BaseProfiles)}.");
        sb.AppendLine($"{report.ArchivesAdded} more downloads ({report.AddedDownloadSize / 1e9:0.00} GB); {report.TotalDownloadSize / 1e9:0.00} GB in all.");
        sb.AppendLine($"{report.Conflicts.Count} loose-file conflicts between the lists" +
                      (report.Conflicts.Count == 0 ? "." : "; the full list is in the report."));
        if (report.ArchiveLoadOrder.Count > 0)
            sb.AppendLine($"{report.ArchiveLoadOrder.Count} overlay archives load first, in the overlay's order.");
        foreach (var w in report.Warnings) sb.AppendLine("⚠ " + w);
        sb.AppendLine();
        sb.AppendLine($"Wrote {output}");
        sb.AppendLine($"Report: {reportPath}");
        return sb.ToString();
    }

    private static string Initials(string name)
    {
        var initials = new string(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).Select(w => char.ToUpperInvariant(w[0])).ToArray());
        return initials.Length is >= 2 and <= 4 ? initials : "Overlay";
    }
}
