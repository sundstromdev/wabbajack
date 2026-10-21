using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Wabbajack.App.Avalonia.ViewModels.Merge;

/// <summary>How a row of the merge list looks: separator headers bold and larger, mods that cannot be taken dimmed.</summary>
public static class MergeConverters
{
    public static readonly IValueConverter HeaderWeight =
        new FuncValueConverter<bool, FontWeight>(header => header ? FontWeight.Bold : FontWeight.Normal);

    public static readonly IValueConverter HeaderSize =
        new FuncValueConverter<bool, double>(header => header ? 15 : 13);

    public static readonly IValueConverter TakeableOpacity =
        new FuncValueConverter<bool, double>(takeable => takeable ? 1.0 : 0.45);
}
