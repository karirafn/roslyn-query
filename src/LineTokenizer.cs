namespace RoslynQuery;

public static class LineTokenizer
{
    public static string[] Tokenize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        List<string> tokens = [];
        int i = 0;

        while (i < line.Length)
        {
            if (line[i] == ' ')
            {
                i++;
                continue;
            }

            if (line[i] == '"')
            {
                i++;
                int start = i;

                while (i < line.Length && line[i] != '"')
                {
                    i++;
                }

                tokens.Add(line[start..i]);

                if (i < line.Length)
                {
                    i++;
                }

                continue;
            }

            int tokenStart = i;

            while (i < line.Length && line[i] != ' ')
            {
                i++;
            }

            tokens.Add(line[tokenStart..i]);
        }

        return [.. tokens];
    }
}
