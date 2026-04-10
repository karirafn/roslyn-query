namespace RoslynQuery;

public sealed class LimitedWriter(TextWriter inner, int maxLines) : TextWriter
{
    private int _linesWritten;

    public int Suppressed { get; private set; }

    public override System.Text.Encoding Encoding => inner.Encoding;

    public override void WriteLine(string? value)
    {
        if (maxLines <= 0 || _linesWritten < maxLines)
        {
            inner.WriteLine(value);
            _linesWritten++;
        }
        else
        {
            Suppressed++;
        }
    }

    public override Task WriteLineAsync(string? value)
    {
        if (maxLines <= 0 || _linesWritten < maxLines)
        {
            _linesWritten++;
            return inner.WriteLineAsync(value);
        }

        Suppressed++;
        return Task.CompletedTask;
    }
}
