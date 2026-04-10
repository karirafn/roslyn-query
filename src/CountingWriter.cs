namespace RoslynQuery;

public sealed class CountingWriter : TextWriter
{
    private const string HeaderPrefix = "# ";

    public int Count { get; private set; }

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        if (value is null || !value.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            Count++;
        }
    }

    public override Task WriteLineAsync(string? value)
    {
        if (value is null || !value.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            Count++;
        }

        return Task.CompletedTask;
    }
}
