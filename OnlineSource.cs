using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;

namespace RnxFotoo;

internal sealed record OnlineSource(string Url, Guid CacheId, string? Title = null)
{
    public string GetHost() => new Uri(Url).IdnHost;
    public string GetName() => string.IsNullOrWhiteSpace(Title) ? GetHost() : Title;

    public static bool TryGetWebUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri)
            && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length != 0;
    }
}

internal static class OnlinePhotoCache
{
    public const int MaxPhotos = 36;
    private const int MaxFileSize = 12 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        MaxAutomaticRedirections = 5
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static string GetPath(Guid id) => Path.Combine(SettingsStore.DirectoryPath, "online", id.ToString("N"));

    public static async Task<OnlineSource?> ImportAsync(Uri page, string title, string[][] candidates,
        CancellationToken cancellationToken)
    {
        var source = new OnlineSource(page.AbsoluteUri, Guid.NewGuid(), title);
        string directory = GetPath(source.CacheId);
        bool keep = false;
        try
        {
            Directory.CreateDirectory(directory);
            var options = new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(Enumerable.Range(0, Math.Min(candidates.Length, MaxPhotos)), options,
                async (index, token) =>
                {
                    if (candidates[index] is not { } group)
                        return;

                    var output = Path.Combine(directory, $"{index + 1:D3}.img");
                    foreach (var url in group.Take(4))
                    {
                        if (OnlineSource.TryGetWebUri(url, out var image) && await DownloadAsync(page, image, output, token))
                            break;
                    }
                });
            cancellationToken.ThrowIfCancellationRequested();
            keep = Directory.EnumerateFiles(directory, "*.img").Any();
            return keep ? source : null;
        }
        finally
        {
            if (!keep)
                Remove(source.CacheId);
        }
    }

    private static async Task<bool> DownloadAsync(Uri page, Uri image, string output, CancellationToken cancellationToken)
    {
        string temporary = Path.ChangeExtension(output, ".tmp");
        using var request = new HttpRequestMessage(HttpMethod.Get, image);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        request.Headers.Referrer = new Uri(page.GetLeftPart(UriPartial.Authority));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RnxFotoo/1.0");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var token = timeout.Token;

        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxFileSize)
                return false;

            string? type = response.Content.Headers.ContentType?.MediaType;
            if (type is not null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                return false;

            using (var input = await response.Content.ReadAsStreamAsync(token))
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                int total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
                {
                    total += count;
                    if (total > MaxFileSize)
                        return false;
                    await file.WriteAsync(buffer.AsMemory(0, count), token);
                }

                await file.FlushAsync(token);
                file.Position = 0;
                using var stream = file.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
                    return false;
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, output);
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or COMException or ArgumentException
                                       or NotSupportedException
                                       || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Remove(Guid id)
    {
        try { Directory.Delete(GetPath(id), recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
