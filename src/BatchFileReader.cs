namespace RoslynQuery;

public static class BatchFileReader
{
    /// <summary>
    /// Returns the file path argument from batch args, or null if stdin should be used.
    /// A file arg is any non-flag, non-solution arg after "batch".
    /// </summary>
    public static string? ResolveFilePath(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        foreach (string arg in args[1..])
        {
            if (arg.StartsWith('-'))
            {
                continue;
            }

            if (arg.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return arg;
        }

        return null;
    }
}
