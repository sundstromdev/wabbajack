namespace Wabbajack.Merge;

/// <summary>
///     The rules and the two modlists cannot be merged as asked. The message is meant for the user and says
///     what to change; nothing has been written when this is thrown.
/// </summary>
public class MergeException : Exception
{
    public MergeException(string message) : base(message)
    {
    }

    public MergeException(IReadOnlyCollection<string> problems)
        : base("The merge cannot go ahead:" + Environment.NewLine +
               string.Join(Environment.NewLine, problems.Select(p => " - " + p)))
    {
        Problems = problems;
    }

    public IReadOnlyCollection<string> Problems { get; } = Array.Empty<string>();
}
