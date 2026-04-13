using System.Xml.Linq;

namespace RoslynQuery;

public static class SlnxReader
{
    public static IReadOnlyList<string> ReadProjectPaths(string slnxPath)
    {
        string dir = Path.GetDirectoryName(slnxPath) ?? "";
        XDocument doc = XDocument.Load(slnxPath);
        List<string> paths = [];
        CollectPaths(doc.Root!, dir, paths);
        return paths;
    }

    private static void CollectPaths(XElement element, string baseDir, List<string> paths)
    {
        foreach (XElement child in element.Elements())
        {
            if (child.Name.LocalName == "Project")
            {
                string? relativePath = (string?)child.Attribute("Path");
                if (relativePath is not null)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(baseDir, relativePath)));
                }
            }
            else if (child.Name.LocalName == "Folder")
            {
                CollectPaths(child, baseDir, paths);
            }
        }
    }
}
