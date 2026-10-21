using System;
using System.Reactive.Subjects;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Wabbajack.Paths;

namespace Wabbajack.App.Avalonia.Services;

/// <summary>
///     Lets Merge lists borrow the gallery to choose a list. While a pick is open the gallery says so and its
///     details pane offers "Use for merge" in place of Install; using a list hands its downloaded Wabbajack file
///     back to the merge screen. A singleton, since the gallery and the merge screen are separate view models.
/// </summary>
public partial class MergeListPicker : ReactiveObject
{
    private readonly Subject<(bool ForBase, AbsolutePath Path)> _picked = new();

    [Reactive] public partial bool IsPicking { get; private set; }
    [Reactive] public partial bool ForBase { get; private set; }
    [Reactive] public partial string Prompt { get; private set; } = "";

    /// <summary>The list chosen, and whether it was asked for as the base or the overlay.</summary>
    public IObservable<(bool ForBase, AbsolutePath Path)> Picked => _picked;

    public void Begin(bool forBase)
    {
        ForBase = forBase;
        Prompt = forBase
            ? "Choosing the base list for Merge lists: open a list and press Use for merge."
            : "Choosing the overlay list for Merge lists: open a list and press Use for merge.";
        IsPicking = true;
    }

    public void Cancel() => IsPicking = false;

    public void Pick(AbsolutePath path)
    {
        var forBase = ForBase;
        IsPicking = false;
        _picked.OnNext((forBase, path));
    }
}
