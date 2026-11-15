using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace RnxFotoo;

public sealed partial class OnlineSourceWindow : Window
{
    private const string Instructions = "Прокрутите страницу, чтобы открыть больше снимков. Можно добавить до 36 снимков за раз.";

    // Each group contains preferred URLs for one visible image, followed by fallback URLs.
    private const string CollectImages = """
        (() => {
            const webUrl = value => {
                if (!value) return null;
                try {
                    const url = new URL(value, document.baseURI);
                    return (url.protocol === 'https:' || url.protocol === 'http:') && !url.username && !url.password
                        ? url.href : null;
                } catch { return null; }
            };
            if (document.contentType.startsWith('image/')) return [[location.href]];

            const groups = [];
            const seen = new Set();
            const add = (...values) => {
                const urls = [...new Set(values.map(webUrl).filter(Boolean))];
                const key = urls[0];
                if (key && !seen.has(key)) {
                    seen.add(key);
                    groups.push(urls);
                }
            };
            for (const image of document.images) {
                if (image.naturalWidth < 120 || image.naturalHeight < 90 || image.getClientRects().length === 0)
                    continue;
                const link = image.closest('a[href]');
                const href = link && webUrl(link.href);
                const parameters = href ? new URL(href).searchParams : null;
                const displayed = image.currentSrc || image.src;
                add(parameters?.get('imgurl') || parameters?.get('img_url') || image.dataset.original,
                    image.dataset.src, displayed);
                if (groups.length >= 36) break;
            }
            if (!groups.length) {
                for (const meta of document.querySelectorAll('meta[property="og:image"], meta[name="twitter:image"]')) {
                    add(meta.content);
                    if (groups.length >= 36) break;
                }
            }
            return groups;
        })()
        """;

    private readonly TaskCompletionSource<OnlineSource?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _import;
    private OnlineSource? _selected;
    private bool _closed;
    private bool _pageReady;
    private ulong _navigationId;

    internal OnlineSourceWindow(OnlineSource? existing)
    {
        InitializeComponent();
        if (existing is not null)
        {
            Query.Text = existing.Url;
            ImportButton.Content = "Обновить подборку";
            string host = existing.GetHost();
            if (host.StartsWith("www.google.", StringComparison.Ordinal))
                Providers.SelectedIndex = 1;
            else if (host.Contains("pinterest.", StringComparison.Ordinal))
                Providers.SelectedIndex = 2;
        }

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Frame.ico"));
        Root.Loaded += Root_Loaded;
        Closed += Window_Closed;
    }

    internal Task<OnlineSource?> PickAsync()
    {
        Activate();
        return _result.Task;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= Root_Loaded;
        Status.Text = "Открываем браузер…";
        try
        {
            string profile = Path.Combine(SettingsStore.DirectoryPath, "browser");
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            if (_closed)
                return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed)
                return;

            var browser = Browser.CoreWebView2;
            browser.Settings.IsWebMessageEnabled = false;
            browser.DownloadStarting += (_, download) => download.Cancel = true;
            browser.NewWindowRequested += (_, request) =>
            {
                request.Handled = true;
                if (_import is null && OnlineSource.TryGetWebUri(request.Uri, out var uri))
                    browser.Navigate(uri.AbsoluteUri);
            };
            browser.NavigationStarting += (sender, navigation) =>
            {
                if (_import is not null || !OnlineSource.TryGetWebUri(navigation.Uri, out _))
                {
                    navigation.Cancel = true;
                    return;
                }

                _navigationId = navigation.NavigationId;
                _pageReady = false;
                Status.Text = "Открываем страницу…";
                UpdateControls();
            };
            browser.SourceChanged += (_, _) =>
            {
                if (_closed)
                    return;
                PageAddress.Text = browser.Source;
                ToolTipService.SetToolTip(PageAddress, browser.Source);
            };
            browser.NavigationCompleted += (_, navigation) =>
            {
                if (_navigationId != navigation.NavigationId || _closed)
                    return;
                _pageReady = navigation.IsSuccess;
                Status.Text = _pageReady ? Instructions : "Страница недоступна. Попробуйте другой запрос или ссылку.";
                UpdateControls();
            };
            browser.HistoryChanged += (_, _) => UpdateControls();
            OpenPage();
            Query.Focus(FocusState.Programmatic);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or IOException
                                       or UnauthorizedAccessException)
        {
            if (!_closed)
                Status.Text = "Не удалось открыть браузер. Проверьте, установлен ли Microsoft Edge WebView2 Runtime.";
        }
    }

    private void OpenPage()
    {
        var browser = Browser.CoreWebView2;
        if (browser is null || _import is not null)
            return;

        string input = Query.Text.Trim();
        if (OnlineSource.TryGetWebUri(input, out var uri))
        {
            browser.Navigate(uri.AbsoluteUri);
            return;
        }

        if (input.Contains("://", StringComparison.Ordinal))
        {
            Status.Text = "Введите запрос или адрес страницы с http:// или https://.";
            return;
        }

        string query = Uri.EscapeDataString(input);
        string address = Providers.SelectedIndex switch
        {
            1 => input.Length == 0 ? "https://images.google.com/" : $"https://www.google.com/search?udm=2&q={query}",
            2 => input.Length == 0 ? "https://www.pinterest.com/" : $"https://www.pinterest.com/search/pins/?q={query}",
            _ => input.Length == 0 ? "https://yandex.ru/images/" : $"https://yandex.ru/images/search?text={query}"
        };
        browser.Navigate(address);
    }

    private void GoButton_Click(object sender, RoutedEventArgs args) => OpenPage();

    private void Query_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            OpenPage();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs args) => Browser.CoreWebView2?.GoBack();

    private async void ImportButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_pageReady || _import is not null || !OnlineSource.TryGetWebUri(Browser.CoreWebView2.Source, out var page))
            return;

        using var import = new CancellationTokenSource();
        _import = import;
        Status.Text = "Загружаем снимки…";
        UpdateControls();

        try
        {
            string title = Browser.CoreWebView2.DocumentTitle.Trim();
            if (title.Length > 160)
                title = title[..160];
            string json = await Browser.ExecuteScriptAsync(CollectImages);
            import.Token.ThrowIfCancellationRequested();
            var candidates = JsonSerializer.Deserialize<string[][]>(json) ?? [];
            if (candidates.Length == 0)
            {
                Status.Text = "На странице пока нет снимков. Откройте результаты поиска, доску или изображение.";
                return;
            }

            var source = await OnlinePhotoCache.ImportAsync(page, title, candidates, import.Token);
            if (_closed)
            {
                if (source is not null)
                    OnlinePhotoCache.Remove(source.CacheId);
                return;
            }
            if (source is null)
            {
                Status.Text = "Не удалось загрузить снимки. Попробуйте другую страницу или прямую ссылку на изображение.";
                return;
            }

            _selected = source;
            Close();
        }
        catch (OperationCanceledException) when (import.IsCancellationRequested) { }
        catch (Exception error) when (error is COMException or JsonException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            if (!_closed)
                Status.Text = "Не удалось сохранить снимки. Попробуйте ещё раз.";
        }
        finally
        {
            _import = null;
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        if (_closed)
            return;
        var browser = Browser.CoreWebView2;
        bool idle = browser is not null && _import is null;
        Browser.IsHitTestVisible = idle;
        GoButton.IsEnabled = idle;
        BackButton.IsEnabled = idle && browser!.CanGoBack;
        ImportButton.IsEnabled = idle && _pageReady;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        if (_selected is null)
            _import?.Cancel();
        Browser.Close();
        _result.TrySetResult(_selected);
    }
}
