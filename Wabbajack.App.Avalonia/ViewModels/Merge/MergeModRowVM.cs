using System.Collections.Generic;
using System.Linq;
using ReactiveUI;
using Wabbajack.Merge;

namespace Wabbajack.App.Avalonia.ViewModels.Merge;

/// <summary>
///     One line of the overlay list: a mod, or the header of the separator it sits under. A header's box ticks
///     or clears every mod under it that can be taken, and shows a mixed state when only some are.
/// </summary>
public class MergeModRowVM : ViewModel
{
    private bool? _isChecked = false;
    private bool _updating;

    private MergeModRowVM(string name, string display, bool isHeader, OverlayMod? mod)
    {
        Name = name;
        Display = display;
        IsHeader = isHeader;
        Mod = mod;
    }

    public string Name { get; }
    public string Display { get; }
    public bool IsHeader { get; }
    public OverlayMod? Mod { get; }
    public MergeModRowVM? Header { get; private set; }
    public List<MergeModRowVM> Children { get; } = new();

    /// <summary>New mods, and duplicates (which the user may want anyway); shared ones are always the base's.</summary>
    public bool CanCheck => IsHeader ? Children.Any(c => c.CanCheck) : Mod!.Status is OverlayModStatus.New or OverlayModStatus.Duplicate;

    public bool IsDisabledInOverlay => Mod is { Enabled: false };

    public string StatusText => Mod?.Status switch
    {
        OverlayModStatus.New => "new",
        OverlayModStatus.Shared => "already in base",
        OverlayModStatus.Duplicate => "same mod as " + string.Join(", ", Mod.DuplicateOf),
        OverlayModStatus.NoFiles => "no files",
        _ => $"{Children.Count(c => c.IsChecked == true)} of {Children.Count(c => c.CanCheck)} selected"
    };

    public bool IsNew => Mod?.Status == OverlayModStatus.New;

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (IsHeader)
            {
                var target = value ?? false;
                _updating = true;
                foreach (var c in Children.Where(c => c.CanCheck)) c.IsChecked = target;
                _updating = false;
                Refresh();
                return;
            }

            var v = CanCheck && value == true;
            if (_isChecked == v) return;
            this.RaiseAndSetIfChanged(ref _isChecked, v);
            Header?.Refresh();
        }
    }

    private void Refresh()
    {
        if (_updating) return;
        var takeable = Children.Where(c => c.CanCheck).ToList();
        bool? state = takeable.Count == 0 || takeable.All(c => c.IsChecked != true) ? false
            : takeable.All(c => c.IsChecked == true) ? true : null;
        this.RaiseAndSetIfChanged(ref _isChecked, state, nameof(IsChecked));
        this.RaisePropertyChanged(nameof(StatusText));
    }

    public static List<MergeModRowVM> Build(IEnumerable<OverlayMod> mods)
    {
        var rows = new List<MergeModRowVM>();
        MergeModRowVM? header = null;
        foreach (var m in mods)
        {
            var sep = m.Separator ?? "(no separator)";
            if (header == null || header.Name != sep)
            {
                var title = sep.EndsWith("_separator") ? sep[..^"_separator".Length] : sep;
                header = new MergeModRowVM(sep, title, true, null);
                rows.Add(header);
            }

            var row = new MergeModRowVM(m.Name, m.Name, false, m) { Header = header };
            header.Children.Add(row);
            rows.Add(row);
        }

        foreach (var h in rows.Where(r => r.IsHeader)) h.Refresh();
        return rows;
    }
}
