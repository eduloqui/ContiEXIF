namespace ContiExif.Services;

public static class AppPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ContiTools");

    public static string Originals => Path.Combine(Root, "Originals");
    public static string ThumbsDb => Path.Combine(Root, "ContiExif-Thumbs.db");
    public static string LegacyCache => Path.Combine(Root, "ContiExif-Cache");
    public static string Settings => Path.Combine(Root, "ContiExif-settings.json");

    public static string DbFor(string folder)
    {
        string name = new DirectoryInfo(folder).Name;
        var invalid = Path.GetInvalidFileNameChars();
        name = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim('_', ' ');
        if (name.Length == 0) name = "Root";
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(Root, i == 1 ? $"ContiExif-{name}.db" : $"ContiExif-{name}_{i}.db");
            if (!File.Exists(p)) return p;
            var root = Database.ReadRoot(p);
            if (string.Equals(root, folder, StringComparison.OrdinalIgnoreCase)) return p;
        }
    }
}
