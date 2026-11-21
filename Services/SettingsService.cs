using System.Text.Json;
using ContiExif.Models;

namespace ContiExif.Services;

public static class SettingsService
{
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.Settings,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
