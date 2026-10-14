namespace RoslynQuery;

public sealed record LoadHealth
{
    public static readonly LoadHealth Complete = new([]);

    public LoadHealth(IReadOnlyList<string> failureMessages)
    {
        ArgumentNullException.ThrowIfNull(failureMessages);
        FailureMessages = failureMessages;
    }

    public IReadOnlyList<string> FailureMessages { get; }

    public int FailureCount => FailureMessages.Count;

    public bool IsPartial => FailureMessages.Count > 0;
}
