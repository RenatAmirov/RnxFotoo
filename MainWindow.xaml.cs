using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;
using Windows.System.Display;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;

namespace RnxFotoo;

public sealed partial class MainWindow : Window
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly AppSettings _settings;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _playbackTimer;
    private readonly DispatcherQueueTimer _controlsTimer;
    private readonly Stopwatch _frameTime = new();
    private readonly DisplayRequest _displayRequest = new();

    private CancellationTokenSource? _scan;
    private CancellationTokenSource? _imageLoad;
    private OnlineSourceWindow? _sourceWindow;
    private OnlineSource? _editingSource;
    private Storyboard? _fade;
    private AppWindowPresenter? _windowedPresenter;
    private List<CatalogPhoto> _photos = [];
    private int _photoIndex = -1;
    private int _unreadableCount;
    private CatalogPhoto? _currentPhoto;
    private bool _ready;
    private bool _closed;
    private bool _scanning;
    private bool _pickingFolder;
    private bool _playing;
    private bool _fullScreen;
    private bool _compact;
    private bool _fullControlsHovered;
    private bool _displayRequested;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load(out var warning);

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => UpdateClock();

        _playbackTimer = DispatcherQueue.CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(80);
        _playbackTimer.Tick += PlaybackTimer_Tick;

        _controlsTimer = DispatcherQueue.CreateTimer();
        _controlsTimer.Interval = TimeSpan.FromSeconds(3);
        _controlsTimer.IsRepeating = false;
        _controlsTimer.Tick += (_, _) => HideFullScreenControls();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = Color.FromArgb(255, 245, 242, 237);
        titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 153, 155, 164);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Frame.ico"));
        SetInitialWindowSize();

        foreach (var interval in AppSettings.Intervals)
            IntervalBox.Items.Add($"{interval} сек.");
        IntervalBox.SelectedIndex = Array.IndexOf(AppSettings.Intervals, _settings.IntervalSeconds);
        FitBox.SelectedIndex = _settings.FillFrame ? 1 : 0;
        SubfoldersToggle.IsOn = _settings.IncludeSubfolders;
        ShuffleToggle.IsOn = _settings.Shuffle;
        FadeToggle.IsOn = _settings.Fade;
        ClockToggle.IsOn = _settings.ShowClock;
        ApplyFit();
        ApplyClock();
        if (warning is not null)
            ShowNotice(warning);

        UpdatePlaybackUi();
        _ready = true;
        Root.Loaded += Root_Loaded;
        Closed += Window_Closed;
    }

    private void SetInitialWindowSize()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0)
            scale = 1;
        int width = Math.Min((int)(1260 * scale), (int)(area.Width * 0.94));
        int height = Math.Min((int)(818 * scale), (int)(area.Height * 0.94));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint hwnd);

    private async void Root_Loaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= Root_Loaded;
        UpdateLayoutMode();
        await RefreshPhotosAsync();
        if (!_closed && !_compact && _currentPhoto is null && _settings.OnlineSources.Count == 0)
            AddFolderButton.Focus(FocusState.Programmatic);
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs args) => await AddFolderAsync();

    private async void AddFolderShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_fullScreen)
            return;
        args.Handled = true;
        await AddFolderAsync();
    }

    private async Task AddFolderAsync()
    {
        if (_pickingFolder)
            return;

        _pickingFolder = true;
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { CommitButtonText = "Добавить папку" };
            var folder = await picker.PickSingleFolderAsync();
            if (_closed || folder is null)
                return;

            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            if (_settings.Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                ShowNotice("Эта папка уже добавлена.", InfoBarSeverity.Informational);
                return;
            }

            _settings.Folders.Add(path);
            SaveSettings();
            await RefreshPhotosAsync();
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_closed)
                ShowNotice("Не удалось открыть папку.");
        }
        finally
        {
            _pickingFolder = false;
        }
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        _settings.Folders.Remove((string)button.Tag);
        SaveSettings();
        AddFolderButton.Focus(FocusState.Programmatic);
        await RefreshPhotosAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs args) => await RefreshPhotosAsync();

    private async void AddOnlineSource_Click(object sender, RoutedEventArgs args) => await EditOnlineSourceAsync(null);

    private async void UpdateOnlineSource_Click(object sender, RoutedEventArgs args)
        => await EditOnlineSourceAsync((OnlineSource)((Button)sender).Tag);

    private async Task EditOnlineSourceAsync(OnlineSource? original)
    {
        if (_sourceWindow is not null)
        {
            _sourceWindow.Activate();
            return;
        }

        var window = new OnlineSourceWindow(original);
        _sourceWindow = window;
        _editingSource = original;
        var source = await window.PickAsync();
        _sourceWindow = null;
        _editingSource = null;
        if (source is null)
            return;
        if (_closed)
        {
            OnlinePhotoCache.Remove(source.CacheId);
            return;
        }

        var replaced = _settings.OnlineSources.Where(item => item == original || item.Url == source.Url).ToArray();
        foreach (var item in replaced)
            _settings.OnlineSources.Remove(item);
        _settings.OnlineSources.Add(source);
        if (SaveSettings())
        {
            foreach (var item in replaced)
                OnlinePhotoCache.Remove(item.CacheId);
        }
        Activate();
        await RefreshPhotosAsync();
    }

    private async void RemoveOnlineSource_Click(object sender, RoutedEventArgs args)
    {
        var source = (OnlineSource)((Button)sender).Tag;
        if (source == _editingSource)
            _sourceWindow?.Close();
        _settings.OnlineSources.Remove(source);
        if (SaveSettings())
            OnlinePhotoCache.Remove(source.CacheId);
        await RefreshPhotosAsync();
    }

    private async Task RefreshPhotosAsync()
    {
        _scan?.Cancel();
        _imageLoad?.Cancel();
        using var scan = new CancellationTokenSource();
        _scan = scan;
        _scanning = true;
        UpdatePlaybackUi();

        try
        {
            var folders = _settings.Folders.ToArray();
            var sources = _settings.OnlineSources.ToArray();
            var includeSubfolders = _settings.IncludeSubfolders;
            var result = await Task.Run(() => PhotoCatalog.Scan(folders, sources, includeSubfolders, scan.Token), scan.Token);
            scan.Token.ThrowIfCancellationRequested();

            RenderFolders(result.Folders);
            RenderOnlineSources(result.OnlineSources);
            if (_settings.Shuffle)
                Random.Shared.Shuffle(result.Photos);
            _photos = result.Photos.ToList();
            _photoIndex = _photos.FindIndex(photo => StringComparer.OrdinalIgnoreCase.Equals(photo.Path, _currentPhoto?.Path));
            _unreadableCount = 0;

            if (_photos.Count == 0)
                ClearPhoto();
            else
                await ShowPhotoAsync(Math.Max(_photoIndex, 0), 1, reuseCurrent: false);
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested) { }
        finally
        {
            if (_scan == scan)
            {
                _scan = null;
                _scanning = false;
                UpdatePlaybackUi();
            }
        }
    }

    private void RenderFolders(FolderScan[] folders)
    {
        FolderRows.Children.Clear();
        EmptyFolders.Visibility = VisibleWhen(folders.Length == 0);
        FolderRows.Visibility = VisibleWhen(folders.Length > 0);
        AddFolderButton.Style = folders.Length == 0 ? (Style)Application.Current.Resources["PrimaryButton"] : null;

        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder.Path);
            if (name.Length == 0)
                name = folder.Path;
            string details = folder.Available ? PhotoCountText(folder.PhotoCount) : "Папка недоступна";
            var remove = SourceButton("\uE711", $"Убрать папку {name}", folder.Path, RemoveFolder_Click);
            FolderRows.Children.Add(SourceRow("\uE8B7", name, details, folder.Path, remove));
        }
    }

    private void RenderOnlineSources(OnlineSourceScan[] sources)
    {
        SourceRows.Children.Clear();
        InternetHint.Visibility = VisibleWhen(sources.Length == 0);
        SourceRows.Visibility = VisibleWhen(sources.Length > 0);
        foreach (var result in sources)
        {
            var source = result.Source;
            string name = source.GetName();
            var update = SourceButton("\uE72C", $"Обновить подборку {name}", source, UpdateOnlineSource_Click);
            var remove = SourceButton("\uE711", $"Убрать источник {name}", source, RemoveOnlineSource_Click);
            SourceRows.Children.Add(SourceRow("\uE774", name, PhotoCountText(result.PhotoCount), source.Url, update, remove));
        }
    }

    private static Grid SourceRow(string glyph, string name, string details, string address, params Button[] actions)
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = 41 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = GetBrush("WarmBrush") });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        label.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        label.Children.Add(new TextBlock
        {
            Text = details,
            FontSize = 12,
            Foreground = GetBrush("MutedTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        ToolTipService.SetToolTip(label, address);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        foreach (var action in actions)
        {
            Grid.SetColumn(action, row.ColumnDefinitions.Count);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(action);
        }
        return row;
    }

    private static Button SourceButton(string glyph, string label, object source, RoutedEventHandler click)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 29,
            Height = 29,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = source,
            Content = new FontIcon { Glyph = glyph, FontSize = 12 }
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += click;
        return button;
    }

    private static string PhotoCountText(int count)
    {
        string noun = (count % 100, count % 10) switch
        {
            (>= 11 and <= 14, _) => "снимков",
            (_, 1) => "снимок",
            (_, >= 2 and <= 4) => "снимка",
            _ => "снимков"
        };
        return $"{count} {noun}";
    }

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];

    private async Task ShowPhotoAsync(int index, int direction, bool reuseCurrent = true)
    {
        using var load = new CancellationTokenSource();
        _imageLoad = load;
        _frameTime.Reset();
        UpdatePlaybackUi();

        try
        {
            while (_photos.Count > 0)
            {
                load.Token.ThrowIfCancellationRequested();
                index = (index + _photos.Count) % _photos.Count;
                var photo = _photos[index];
                bool samePath = StringComparer.OrdinalIgnoreCase.Equals(photo.Path, _currentPhoto?.Path);
                if (reuseCurrent && samePath)
                {
                    _photoIndex = index;
                    return;
                }

                ImageSource source;
                try
                {
                    source = await PhotoCatalog.LoadPhotoAsync(photo.Path, load.Token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException
                                               or ArgumentException or NotSupportedException)
                {
                    load.Token.ThrowIfCancellationRequested();
                    _photos.RemoveAt(index);
                    _unreadableCount++;
                    if (direction < 0)
                        index--;
                    continue;
                }

                _photoIndex = index;
                _currentPhoto = photo;
                PresentPhoto(source, animate: !samePath);
                return;
            }

            ClearPhoto();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        finally
        {
            if (_imageLoad == load)
            {
                _imageLoad = null;
                UpdatePlaybackUi();
            }
        }
    }

    private void PresentPhoto(ImageSource source, bool animate)
    {
        EndTransition();
        animate = animate && _settings.Fade && CurrentPhoto.Source is not null;
        DemoPhoto.Visibility = Visibility.Collapsed;
        ExampleBadge.Visibility = Visibility.Collapsed;

        if (animate)
        {
            PhotoUnderlay.Source = CurrentPhoto.Source;
            CurrentPhoto.Opacity = 0;
        }
        CurrentPhoto.Source = source;
        if (!animate)
            return;

        var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(450) };
        Storyboard.SetTarget(opacity, CurrentPhoto);
        Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));
        var fade = new Storyboard();
        fade.Children.Add(opacity);
        fade.Completed += (_, _) =>
        {
            if (_fade == fade)
                EndTransition();
        };
        _fade = fade;
        fade.Begin();
    }

    private void EndTransition()
    {
        var fade = _fade;
        _fade = null;
        fade?.Stop();
        CurrentPhoto.Opacity = 1;
        PhotoUnderlay.Source = null;
    }

    private void ClearPhoto()
    {
        EndTransition();
        _photoIndex = -1;
        _currentPhoto = null;
        _playing = false;
        CurrentPhoto.Source = null;
        DemoPhoto.Visibility = Visibility.Visible;
        ExampleBadge.Visibility = Visibility.Visible;
        if (_fullScreen)
            ToggleFullScreen();
    }

    private async Task StepAsync(int direction)
    {
        if (_closed || _scanning || _imageLoad is not null || _photos.Count < 2)
            return;
        await ShowPhotoAsync(_photoIndex + direction, direction);
    }

    private async void Previous_Click(object sender, RoutedEventArgs args) => await StepAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs args) => await StepAsync(1);
    private void Play_Click(object sender, RoutedEventArgs args) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_currentPhoto is null || _photos.Count < 2 || _scanning)
            return;

        _playing = !_playing;
        UpdatePlaybackUi();
    }

    private async void PlaybackTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        UpdateProgress();
        if (_frameTime.Elapsed.TotalSeconds >= _settings.IntervalSeconds)
            await StepAsync(1);
    }

    private void UpdateProgress()
    {
        PlaybackProgress.Value = Math.Min(100, 100 * _frameTime.Elapsed.TotalSeconds / _settings.IntervalSeconds);
    }

    private void UpdatePlaybackUi()
    {
        if (_closed)
            return;

        bool hasPhoto = _currentPhoto is not null;
        bool canPlay = hasPhoto && _photos.Count > 1 && !_scanning;
        bool canStep = canPlay && _imageLoad is null;
        if (!_scanning && _imageLoad is null && _photos.Count < 2)
        {
            _playing = false;
            _frameTime.Reset();
        }

        if (_playing && canStep)
        {
            _frameTime.Start();
            if (!_playbackTimer.IsRunning)
                _playbackTimer.Start();
        }
        else
        {
            _frameTime.Stop();
            _playbackTimer.Stop();
        }

        FullScreenButton.IsEnabled = hasPhoto && !_scanning;
        PlayButton.IsEnabled = FullPlay.IsEnabled = canPlay;
        PreviousButton.IsEnabled = FullPrevious.IsEnabled = canStep;
        NextButton.IsEnabled = FullNext.IsEnabled = canStep;
        RefreshButton.IsEnabled = _settings.Folders.Count > 0 && !_scanning;
        PlayIcon.Glyph = FullPlayIcon.Glyph = _playing ? "\uE769" : "\uE768";
        string playLabel = _playing ? "Пауза" : "Воспроизвести";
        foreach (var button in new[] { PlayButton, FullPlay })
        {
            AutomationProperties.SetName(button, playLabel);
            ToolTipService.SetToolTip(button, $"{playLabel} (Пробел)");
        }

        PhotoCounter.Text = hasPhoto ? $"{Math.Max(0, _photoIndex + 1)} / {_photos.Count}" : "0 / 0";
        if (_currentPhoto is { } photo)
        {
            PhotoName.Text = photo.Source is null ? Path.GetFileName(photo.Path)
                : $"Интернет-снимок {Path.GetFileNameWithoutExtension(photo.Path).TrimStart('0')}";
            PhotoFolder.Text = photo.Source?.GetHost() ?? Path.GetDirectoryName(photo.Path) ?? "";
            ToolTipService.SetToolTip(PhotoDetails, photo.Source?.Url ?? photo.Path);
        }
        else
        {
            bool hasSources = _settings.Folders.Count + _settings.OnlineSources.Count > 0;
            PhotoName.Text = _scanning && hasSources ? "Поиск фотографий…" : "Нет фотографий";
            PhotoFolder.Text = !hasSources ? "Добавьте папку или интернет-источник"
                : _unreadableCount > 0 ? "Не удалось открыть снимки" : "Проверьте выбранные источники";
            ToolTipService.SetToolTip(PhotoDetails, null);
        }

        UpdateProgress();
        UpdateDisplayRequest();
    }

    private void UpdateDisplayRequest()
    {
        bool keepOn = !_closed && _fullScreen && _currentPhoto is not null;
        if (keepOn == _displayRequested)
            return;
        if (keepOn)
            _displayRequest.RequestActive();
        else
            _displayRequest.RequestRelease();
        _displayRequested = keepOn;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs args) => ToggleFullScreen();

    private void FullScreenShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleFullScreen();
        args.Handled = true;
    }

    private void EscapeShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_fullScreen)
        {
            ToggleFullScreen();
            args.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen && (_currentPhoto is null || _scanning))
            return;

        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _windowedPresenter = AppWindow.Presenter;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _playing = _photos.Count > 1;
        }
        else
        {
            _controlsTimer.Stop();
            FullScreenControls.Visibility = Visibility.Collapsed;
            _fullControlsHovered = false;
            if (_windowedPresenter is not null)
                AppWindow.SetPresenter(_windowedPresenter);
            else
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            _windowedPresenter = null;
        }

        TitleBarDragRegion.Visibility = Header.Visibility = PlaybackBar.Visibility = VisibleWhen(!_fullScreen);
        Notice.Visibility = VisibleWhen(!_fullScreen);
        FrameCard.CornerRadius = new CornerRadius(_fullScreen ? 0 : 13);
        FrameCard.BorderThickness = new Thickness(_fullScreen ? 0 : 1);
        PreviewLayout.RowSpacing = _fullScreen ? 0 : 12;
        ClockText.Margin = _fullScreen ? new Thickness(34, 56, 34, 32) : new Thickness(23, 40, 23, 23);
        ClockTime.FontSize = _fullScreen ? 82 : 66;
        ClockTime.LineHeight = _fullScreen ? 90 : 74;
        UpdateLayoutMode(force: true);
        UpdatePlaybackUi();
        PhotoFocus.Focus(FocusState.Programmatic);
        if (_fullScreen)
            RevealFullScreenControls();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutMode();

    private void UpdateLayoutMode(bool force = false)
    {
        if (!_ready || Root.ActualWidth <= 0)
            return;

        double width = Root.ActualWidth;
        bool compact = width < 915;
        if (force || compact != _compact || (!compact && !_fullScreen && !Workspace.IsPaneOpen))
        {
            _compact = compact;
            Workspace.IsPaneOpen = !_fullScreen && !compact;
            Workspace.DisplayMode = compact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
            Sidebar.Padding = compact ? new Thickness(14, 12, 16, 22) : new Thickness(0, 0, 24, 22);
        }

        Workspace.OpenPaneLength = Math.Min(348, Math.Max(0, width - 8));
        PaneButton.Visibility = VisibleWhen(compact);
        HeaderSubtitle.Visibility = VisibleWhen(width >= 365);
        FullScreenLabel.Visibility = VisibleWhen(width >= 490);
        PhotoDetailsIcon.Visibility = VisibleWhen(width >= 500);
        PreviewLayout.Margin = _fullScreen ? new Thickness(0) : new Thickness(24, 0, 24, 22);
    }

    private void PaneButton_Click(object sender, RoutedEventArgs args) => Workspace.IsPaneOpen = !Workspace.IsPaneOpen;

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_fullScreen && (FocusManager.GetFocusedElement(Root.XamlRoot) as ContentControl) != PhotoFocus)
            return;

        switch (args.Key)
        {
            case VirtualKey.Space:
                args.Handled = true;
                TogglePlayback();
                break;
            case VirtualKey.Left:
            case VirtualKey.Right:
                args.Handled = true;
                await StepAsync(args.Key == VirtualKey.Left ? -1 : 1);
                break;
        }
    }

    private void PhotoSurface_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (!_fullControlsHovered)
            PhotoFocus.Focus(FocusState.Pointer);
        RevealFullScreenControls();
    }

    private void PhotoSurface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (!_fullControlsHovered)
            ToggleFullScreen();
    }

    private void PhotoSurface_PointerMoved(object sender, PointerRoutedEventArgs args) => RevealFullScreenControls();

    private void FullScreenControls_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _fullControlsHovered = true;
        _controlsTimer.Stop();
    }

    private void FullScreenControls_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _fullControlsHovered = false;
        RevealFullScreenControls();
    }

    private void RevealFullScreenControls()
    {
        if (!_fullScreen)
            return;
        FullScreenControls.Visibility = Visibility.Visible;
        _controlsTimer.Stop();
        if (!_fullControlsHovered)
            _controlsTimer.Start();
    }

    private void HideFullScreenControls()
    {
        if (!_fullScreen)
            return;
        PhotoFocus.Focus(FocusState.Programmatic);
        FullScreenControls.Visibility = Visibility.Collapsed;
    }

    private async void SubfoldersToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_ready)
            return;
        _settings.IncludeSubfolders = SubfoldersToggle.IsOn;
        SaveSettings();
        await RefreshPhotosAsync();
    }

    private async void ShuffleToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_ready)
            return;
        _settings.Shuffle = ShuffleToggle.IsOn;
        SaveSettings();
        await RefreshPhotosAsync();
    }

    private void IntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || IntervalBox.SelectedIndex < 0)
            return;
        _settings.IntervalSeconds = AppSettings.Intervals[IntervalBox.SelectedIndex];
        _frameTime.Reset();
        SaveSettings();
        UpdatePlaybackUi();
    }

    private void FitBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || FitBox.SelectedIndex < 0)
            return;
        _settings.FillFrame = FitBox.SelectedIndex == 1;
        ApplyFit();
        SaveSettings();
    }

    private void FadeToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_ready)
            return;
        _settings.Fade = FadeToggle.IsOn;
        if (!_settings.Fade)
            EndTransition();
        SaveSettings();
    }

    private void ClockToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_ready)
            return;
        _settings.ShowClock = ClockToggle.IsOn;
        ApplyClock();
        SaveSettings();
    }

    private void ApplyFit() => CurrentPhoto.Stretch = PhotoUnderlay.Stretch = _settings.FillFrame ? Stretch.UniformToFill : Stretch.Uniform;

    private void ApplyClock()
    {
        ClockOverlay.Visibility = VisibleWhen(_settings.ShowClock);
        if (_settings.ShowClock)
        {
            UpdateClock();
            _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("HH:mm", Russian);
        ClockDate.Text = now.ToString("d MMMM · dddd", Russian);
    }

    private static Visibility VisibleWhen(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private bool SaveSettings()
    {
        Notice.IsOpen = false;
        if (SettingsStore.TrySave(_settings))
            return true;

        ShowNotice("Не удалось сохранить настройки. Изменения действуют до закрытия приложения.");
        return false;
    }

    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        Notice.Severity = severity;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _sourceWindow?.Close();
        _scan?.Cancel();
        _imageLoad?.Cancel();
        _playbackTimer.Stop();
        _controlsTimer.Stop();
        _clockTimer.Stop();
        UpdateDisplayRequest();
    }
}
