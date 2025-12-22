namespace JiraPriorityScore.Utils;

public static class AppSettingsLocator
{
    public static string? Find(string fileName)
    {
        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        }.Where(path => !string.IsNullOrWhiteSpace(path))
         .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in searchRoots)
        {
            var directory = new DirectoryInfo(root);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}
