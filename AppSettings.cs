using System.Text.Json;

namespace RnxFotoo;

internal sealed class AppSettings
{
    public static readonly int[] Intervals = [3, 5, 10, 15, 30, 60];

    public List<string> Folders { get; set; } = [];
    public List<OnlineSource> OnlineSources { get; set; } = [];
    public int IntervalSeconds { get; set; } = 10;
    public bool IncludeSubfolders { get; set; } = true;
    public bool Shuffle { get; set; }
    public bool FillFrame { get; set; }
    public bool Fade { get; set; } = true;
    public bool ShowClock { get; set; } = true;
}

internal static class SettingsStore
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RnxFotoo");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (settings is null)
                throw new JsonException();

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in settings.Folders ?? [])
            {
                if (folder is not null && Path.IsPathFullyQualified(folder))
                    folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
            }

            settings.Folders = folders.ToList();
            var sourceUrls = new HashSet<string>(StringComparer.Ordinal);
            var cacheIds = new HashSet<Guid>();
            var sources = new List<OnlineSource>();
            foreach (var source in settings.OnlineSources ?? [])
            {
                if (source is null || source.CacheId == Guid.Empty || !OnlineSource.TryGetWebUri(source.Url, out var uri)
                    || sourceUrls.Contains(uri.AbsoluteUri) || !cacheIds.Add(source.CacheId))
                    continue;

                sourceUrls.Add(uri.AbsoluteUri);
                sources.Add(source with { Url = uri.AbsoluteUri });
            }
            settings.OnlineSources = sources;
            if (!AppSettings.Intervals.Contains(settings.IntervalSeconds))
                settings.IntervalSeconds = 10;
            return settings;
        }
        catch (FileNotFoundException)
        {
            return new AppSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new AppSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warning = "Не удалось прочитать настройки. Загружены начальные значения.";
            return new AppSettings();
        }
    }

    public static bool TrySave(AppSettings settings)
    {
        string temporaryPath = Path.Combine(DirectoryPath, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporaryPath); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
