using Avalonia.ReactiveUI;
using ReactiveUI;
using Wabbajack.App.Avalonia.ViewModels.Merge;

namespace Wabbajack.App.Avalonia.Views.Merge;

public partial class MergeView : ReactiveUserControl<MergeVM>
{
    public MergeView()
    {
        InitializeComponent();
        this.WhenActivated(_ => { });
    }
}
