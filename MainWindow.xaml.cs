using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MiniMediaControl;

public partial class MainWindow : Window
{
    private record Settings(double Left, double Top);

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniMediaControl", "settings.json");

    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private List<GlobalSystemMediaTransportControlsSession> _sessions = new();
    private GlobalSystemMediaTransportControlsSession? _current;
    private int _index;

    private bool _seeking, _sliding, _swipeActive;
    private double _swipeStartX;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _topTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        RestorePosition();

        _tick.Tick += (_, _) => UpdateTimeline();
        _tick.Start();
        _topTimer.Tick += (_, _) => ReassertTopmost();
        _topTimer.Start();

        Loaded += async (_, _) =>
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (_, _) => Dispatcher.BeginInvoke(RefreshSessions);
            RefreshSessions();
        };
    }

    // ---------- Session handling ----------

    private void RefreshSessions()
    {
        if (_mgr == null) return;

        var prevId = _current?.SourceAppUserModelId;
        _sessions = _mgr.GetSessions().ToList();

        if (_sessions.Count == 0)
        {
            Detach();
            _current = null;
            Hide();
            return;
        }

        if (!IsVisible) Show();

        int idx = prevId == null ? -1 : _sessions.FindIndex(s => s.SourceAppUserModelId == prevId);
        if (idx < 0) idx = Math.Min(_index, _sessions.Count - 1);
        Select(idx);
    }

    private void Select(int index)
    {
        Detach();
        _index = index;
        _current = _sessions[index];
        _current.MediaPropertiesChanged += OnSessionChanged;
        _current.PlaybackInfoChanged += OnSessionChanged;
        _current.TimelinePropertiesChanged += OnSessionChanged;
        _ = UpdateMediaAsync();
    }

    private void Detach()
    {
        if (_current == null) return;
        _current.MediaPropertiesChanged -= OnSessionChanged;
        _current.PlaybackInfoChanged -= OnSessionChanged;
        _current.TimelinePropertiesChanged -= OnSessionChanged;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession s, object e) =>
        Dispatcher.BeginInvoke(() => _ = UpdateMediaAsync());

    private async Task UpdateMediaAsync()
    {
        var session = _current;
        if (session == null) return;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (session != _current) return; // switched while awaiting

            TitleText.Text = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
            ArtistText.Text = props.Artist;

            var bmp = await LoadThumbAsync(props.Thumbnail);
            if (session != _current) return;
            ArtBrush.ImageSource = bmp; // null shows the ♪ placeholder

            var status = session.GetPlaybackInfo()?.PlaybackStatus;
            PlayBtn.Content = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ? "\uE769" : "\uE768";

            var app = session.SourceAppUserModelId ?? "";
            if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app[..^4];
            var dots = string.Concat(Enumerable.Range(0, _sessions.Count).Select(i => i == _index ? "● " : "○ "));
            // SourceText.Text = $"{app}  ({_index + 1}/{_sessions.Count})  {dots}";
            SourceText.Text = $"{dots}";

            UpdateTimeline();
        }
        catch { /* session may have vanished */ }
    }

    private static async Task<BitmapImage?> LoadThumbAsync(IRandomAccessStreamReference? reference)
    {
        if (reference == null) return null;
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void UpdateTimeline()
    {
        if (_current == null || _seeking) return;
        try
        {
            var t = _current.GetTimelineProperties();
            var end = t.EndTime - t.StartTime;
            var pos = t.Position;

            var playing = _current.GetPlaybackInfo()?.PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing) pos += DateTimeOffset.Now - t.LastUpdatedTime;
            if (pos > end) pos = end;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

            bool seekable = end.TotalSeconds > 0;
            SeekSlider.IsEnabled = seekable;
            SeekSlider.Maximum = seekable ? end.TotalSeconds : 1;
            SeekSlider.Value = seekable ? pos.TotalSeconds : 0;
            TimeText.Text = seekable ? $"{Fmt(pos)} / {Fmt(end)}" : "";
        }
        catch { }
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    // ---------- Playback controls ----------

    private async void PlayPause_Click(object s, RoutedEventArgs e) { if (_current != null) await _current.TryTogglePlayPauseAsync(); }
    private async void Prev_Click(object s, RoutedEventArgs e) { if (_current != null) await _current.TrySkipPreviousAsync(); }
    private async void Next_Click(object s, RoutedEventArgs e) { if (_current != null) await _current.TrySkipNextAsync(); }

    private void Seek_Down(object s, MouseButtonEventArgs e) => _seeking = true;

    private async void Seek_Up(object s, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        // Let the slider apply the click position first.
        await Dispatcher.Yield(DispatcherPriority.Input);
        var ticks = (long)(SeekSlider.Value * TimeSpan.TicksPerSecond);
        _seeking = false;
        if (_current != null) await _current.TryChangePlaybackPositionAsync(ticks);
    }

    // ---------- Slide between sources ----------

    private void PrevSource_Click(object s, RoutedEventArgs e) => Slide(-1);
    private void NextSource_Click(object s, RoutedEventArgs e) => Slide(+1);

    private void Window_MouseWheel(object s, MouseWheelEventArgs e) => Slide(e.Delta < 0 ? +1 : -1);

    private void Card_PreviewDown(object s, MouseButtonEventArgs e)
    {
        _swipeActive = !IsInside<System.Windows.Controls.Slider>(e.OriginalSource as DependencyObject);
        _swipeStartX = e.GetPosition(this).X;
    }

    private void Card_PreviewUp(object s, MouseButtonEventArgs e)
    {
        if (!_swipeActive) return;
        _swipeActive = false;
        double dx = e.GetPosition(this).X - _swipeStartX;
        if (Math.Abs(dx) > 40) Slide(dx < 0 ? +1 : -1); // swipe left = next source
    }

    private static bool IsInside<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Slide(int dir)
    {
        if (_sessions.Count < 2 || _sliding) return;
        _sliding = true;

        int next = (_index + dir + _sessions.Count) % _sessions.Count;
        double w = ActualWidth;

        var outAnim = new DoubleAnimation(0, -dir * w, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };

        outAnim.Completed += (_, _) =>
        {
            Select(next);
            var inAnim = new DoubleAnimation(dir * w, 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            inAnim.Completed += (_, _) => _sliding = false;
            Shift.BeginAnimation(TranslateTransform.XProperty, inAnim);
        };

        Shift.BeginAnimation(TranslateTransform.XProperty, outAnim);
    }

    // ---------- Window behavior: topmost, no focus steal, position ----------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private void ReassertTopmost()
    {
        if (!IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void Grip_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Grip_MouseUp(object s, MouseButtonEventArgs e) => SavePosition();

    private void RestorePosition()
    {
        double left = double.NaN, top = double.NaN;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var st = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
                if (st != null) { left = st.Left; top = st.Top; }
            }
        }
        catch { }

        // Validate against the virtual screen so it never restores off-screen.
        bool onScreen = !double.IsNaN(left) && !double.IsNaN(top)
            && left + 60 > SystemParameters.VirtualScreenLeft
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
            && top + 30 > SystemParameters.VirtualScreenTop
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30;

        if (onScreen) { Left = left; Top = top; }
        else DefaultPosition();
    }

    private void DefaultPosition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Bottom - Height - 16;
    }

    private void SavePosition()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(Left, Top)));
        }
        catch { }
    }

    private void ResetPosition_Click(object s, RoutedEventArgs e) { DefaultPosition(); SavePosition(); }
    private void Exit_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();
    private void Window_Closing(object? s, System.ComponentModel.CancelEventArgs e) => SavePosition();

    // private static readonly string NamesPath = Path.Combine(
    // Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    // "MiniMediaControl", "appnames.json");

    // private Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    // private void LoadNames()
    // {
    //     try
    //     {
    //         if (!File.Exists(NamesPath))
    //         {
    //             Directory.CreateDirectory(Path.GetDirectoryName(NamesPath)!);
    //             var sample = new Dictionary<string, string>
    //             {
    //                 ["Spotify"] = "Spotify",
    //                 ["msedge"] = "Edge",
    //                 ["chrome"] = "Chrome",
    //                 ["ZuneMusic"] = "Media Player"
    //             };
    //             File.WriteAllText(NamesPath,
    //                 JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true }));
    //         }

    //         var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(NamesPath));
    //         if (d != null) _names = new(d, StringComparer.OrdinalIgnoreCase);
    //     }
    //     catch { /* keep defaults */ }
    // }

    // // Raw id -> trimmed name -> JSON override
    // private string FriendlyName(string? id)
    // {
    //     if (string.IsNullOrWhiteSpace(id)) return "Unknown";
    //     if (_names.TryGetValue(id, out var exact)) return exact;   // match on the raw id
    //     var trimmed = TrimAppId(id);
    //     return _names.TryGetValue(trimmed, out var renamed) ? renamed : trimmed;
    // }

    // private static string TrimAppId(string id)
    // {
    //     var s = id;
    //     if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];

    //     // Store apps look like "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"
    //     int bang = s.IndexOf('!');
    //     if (bang > 0)
    //     {
    //         s = s[..bang];
    //         int us = s.IndexOf('_');
    //         if (us > 0) s = s[..us];
    //     }

    //     // "Microsoft.ZuneMusic" -> "ZuneMusic"
    //     int dot = s.LastIndexOf('.');
    //     if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];

    //     return s;
    // }
}
