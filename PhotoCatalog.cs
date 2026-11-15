using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace RnxFotoo;

internal sealed record FolderScan(string Path, int PhotoCount, bool Available);
internal sealed record OnlineSourceScan(OnlineSource Source, int PhotoCount);
internal sealed record CatalogPhoto(string Path, OnlineSource? Source = null);
internal sealed record CatalogScan(CatalogPhoto[] Photos, FolderScan[] Folders, OnlineSourceScan[] OnlineSources);

internal static class PhotoCatalog
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif"
    };

    public static CatalogScan Scan(string[] folders, OnlineSource[] sources, bool includeSubfolders,
        CancellationToken cancellationToken)
    {
        var allPhotos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<FolderScan>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = 0;
            try
            {
                // Check the root separately so an inaccessible source is visible in the settings.
                using (var root = Directory.EnumerateFileSystemEntries(folder).GetEnumerator())
                    root.MoveNext();

                foreach (var path in Directory.EnumerateFiles(folder, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Extensions.Contains(Path.GetExtension(path)))
                    {
                        allPhotos.Add(path);
                        count++;
                    }
                }

                results.Add(new FolderScan(folder, count, true));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                results.Add(new FolderScan(folder, count, false));
            }
        }

        var photos = allPhotos.Order(StringComparer.OrdinalIgnoreCase).Select(path => new CatalogPhoto(path)).ToList();
        var onlineResults = new List<OnlineSourceScan>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = photos.Count;
            try
            {
                foreach (var path in Directory.GetFiles(OnlinePhotoCache.GetPath(source.CacheId), "*.img").Order(StringComparer.Ordinal))
                    photos.Add(new CatalogPhoto(path, source));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            onlineResults.Add(new OnlineSourceScan(source, photos.Count - start));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CatalogScan(photos.ToArray(), results.ToArray(), onlineResults.ToArray());
    }

    public static async Task<SoftwareBitmapSource> LoadPhotoAsync(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        const int maxDimension = 4096;
        var scale = Math.Min(1.0, (double)maxDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        return source;
    }
}
