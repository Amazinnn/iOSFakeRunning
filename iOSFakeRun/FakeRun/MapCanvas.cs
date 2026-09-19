using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace iOSFakeRun.FakeRun;

/// <summary>
/// Fetches tiles and memoises them in memory and on disk. Tiles are decoded and frozen on
/// background threads, so the render pass can draw them without any further synchronisation.
/// </summary>
internal sealed class TileStore
{
    private const int MaxConcurrentRequests = 6;

    /// <summary>Past this many dead tiles the negative cache is dropped, so a network blip can recover.</summary>
    private const int MaxFailedEntries = 300;

    /// <summary>
    /// Ceiling on the on-disk tile cache. Nothing else bounds it, and the imagery layers cost up to
    /// 90 KB a tile, so without a cap it would grow for as long as the app keeps being used.
    /// </summary>
    private const long MaxCacheBytes = 192L * 1024 * 1024;

    /// <summary>How far to trim past the ceiling, so the trim does not immediately run again.</summary>
    private const long CacheTrimTargetBytes = MaxCacheBytes * 3 / 4;

    private static readonly HttpClient Client = CreateClient();
    private static readonly string CacheRoot = ResolveCacheRoot();

    private static long _cacheBytes;
    private static int _trimRunning;

    /// <summary>
    /// Ceiling on decoded tiles held in memory. A decoded bitmap sits in unmanaged memory (256 KB a
    /// tile) and nothing else ever releases one, so without a cap a session that pans and zoomes
    /// around accumulates them for the life of the process — gigabytes on a long run.
    /// </summary>
    private const int MaxReadyTiles = 1024;

    private readonly ConcurrentDictionary<string, TileEntry> _ready = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRequests);
    private readonly Dispatcher _dispatcher;

    private int _notificationPending;

    public TileStore(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        // Sizing the cache means walking it, so it happens off the UI thread.
        RequestTrim();
    }

    /// <summary>Bytes held in the on-disk tile cache, measured at startup and tracked from there.</summary>
    public static long CacheBytes => Interlocked.Read(ref _cacheBytes);

    /// <summary>The ceiling the cache is trimmed back to.</summary>
    public static long CacheLimitBytes => MaxCacheBytes;

    /// <summary>Deletes every cached tile.</summary>
    public static void ClearCache()
    {
        try
        {
            if (Directory.Exists(CacheRoot))
            {
                Directory.Delete(CacheRoot, true);
            }

            Directory.CreateDirectory(CacheRoot);
        }
        catch (Exception)
        {
            // Housekeeping only; a cache that cannot be cleared costs disk but breaks nothing.
        }

        Interlocked.Exchange(ref _cacheBytes, 0);
    }

    /// <summary>Raised on the UI thread once a tile has become drawable.</summary>
    public event Action? Changed;

    /// <summary>
    /// Returns the tile when it is already in memory, otherwise starts a fetch and returns null.
    /// Called from the render pass, so it must never block.
    /// </summary>
    public BitmapSource? TryGet(MapTileSource source, bool overlay, int zoom, int x, int y)
    {
        var key = $"{source.Key}{(overlay ? "-overlay" : string.Empty)}/{zoom}/{x}/{y}";

        if (_ready.TryGetValue(key, out var entry))
        {
            entry.LastAccess = Environment.TickCount64;
            return entry.Bitmap;
        }

        if (_failed.ContainsKey(key) || !_inFlight.TryAdd(key, 0))
        {
            return null;
        }

        var template = overlay ? source.OverlayTemplate! : source.UrlTemplate;
        _ = FetchAsync(key, source.BuildUrl(template, zoom, x, y));

        return null;
    }

    private async Task FetchAsync(string key, string url)
    {
        BitmapSource? bitmap = null;

        try
        {
            bitmap = await LoadAsync(key, url).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A tile that cannot be fetched is simply not drawn.
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }

        if (bitmap == null)
        {
            if (_failed.Count >= MaxFailedEntries)
            {
                _failed.Clear();
            }

            _failed.TryAdd(key, 0);
        }
        else
        {
            _ready[key] = new TileEntry(bitmap);
            EvictReadyTiles();
        }

        NotifyChanged();
    }

    /// <summary>
    /// Keeps the decoded-tile cache bounded, evicting least recently used entries. Runs on whatever
    /// thread completed a fetch; the scan is cheap against a handful of insertions per second, and
    /// a concurrent eviction at worst removes one tile more than strictly needed.
    /// </summary>
    private void EvictReadyTiles()
    {
        var overshoot = _ready.Count - MaxReadyTiles;

        if (overshoot <= 0)
        {
            return;
        }

        var order = new List<string>(_ready.Count);

        foreach (var pair in _ready)
        {
            order.Add(pair.Key);
        }

        order.Sort((a, b) => _ready[a].LastAccess.CompareTo(_ready[b].LastAccess));

        for (var i = 0; i < overshoot && i < order.Count; i++)
        {
            _ready.TryRemove(order[i], out _);
        }
    }

    /// <summary>A decoded tile plus the tick it was last used at, for eviction.</summary>
    private sealed class TileEntry
    {
        public TileEntry(BitmapSource bitmap)
        {
            Bitmap = bitmap;
            LastAccess = Environment.TickCount64;
        }

        public BitmapSource Bitmap { get; }

        public long LastAccess;
    }

    /// <summary>Coalesces bursts of completions into a single redraw request.</summary>
    private void NotifyChanged()
    {
        if (Interlocked.Exchange(ref _notificationPending, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
        {
            Interlocked.Exchange(ref _notificationPending, 0);
            Changed?.Invoke();
        }));
    }

    private async Task<BitmapSource?> LoadAsync(string key, string url)
    {
        var cachePath = Path.Combine(CacheRoot, key.Replace('/', Path.DirectorySeparatorChar) + ".tile");

        var data = await ReadCacheAsync(cachePath).ConfigureAwait(false);

        if (data == null)
        {
            data = await DownloadAsync(url).ConfigureAwait(false);

            if (data == null)
            {
                return null;
            }

            await WriteCacheAsync(cachePath, data).ConfigureAwait(false);
        }

        return Decode(data);
    }

    private static async Task<byte[]?> ReadCacheAsync(string path)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path).ConfigureAwait(false) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(string path, byte[] data)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Caching is an optimisation; failing to write must not fail the tile.
            return;
        }

        if (Interlocked.Add(ref _cacheBytes, data.Length) > MaxCacheBytes)
        {
            RequestTrim();
        }
    }

    /// <summary>
    /// Runs a trim unless one is already going. The ceiling only matters over a long session, so the
    /// work is fire-and-forget and never blocks a tile.
    /// </summary>
    private static void RequestTrim()
    {
        if (Interlocked.Exchange(ref _trimRunning, 1) == 1)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                TrimCache();
            }
            finally
            {
                Interlocked.Exchange(ref _trimRunning, 0);
            }
        });
    }

    /// <summary>
    /// Measures the cache and, when it is over the ceiling, deletes the oldest tiles until it drops
    /// back under. Runs off the UI thread, and never concurrently with itself.
    /// </summary>
    private static void TrimCache()
    {
        try
        {
            var directory = new DirectoryInfo(CacheRoot);

            if (!directory.Exists)
            {
                Interlocked.Exchange(ref _cacheBytes, 0);
                return;
            }

            var files = directory.GetFiles("*.tile", SearchOption.AllDirectories);
            var total = 0L;

            foreach (var file in files)
            {
                total += file.Length;
            }

            // The measured total is authoritative: writes that raced with it are already on disk.
            Interlocked.Exchange(ref _cacheBytes, total);

            if (total <= MaxCacheBytes)
            {
                return;
            }

            // Oldest first. A route being watched right now was fetched recently, so it survives.
            Array.Sort(files, (left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));

            foreach (var file in files)
            {
                if (total <= CacheTrimTargetBytes)
                {
                    break;
                }

                try
                {
                    var length = file.Length;

                    file.Delete();
                    total -= length;
                }
                catch (Exception)
                {
                    // A tile that cannot be deleted simply stays in the count.
                }
            }

            Interlocked.Exchange(ref _cacheBytes, total);
        }
        catch (Exception)
        {
            // Housekeeping only; a failure must not disturb tile loading.
        }
    }

    private async Task<byte[]?> DownloadAsync(string url)
    {
        await _concurrency.WaitAsync().ConfigureAwait(false);

        try
        {
            using var response = await Client.GetAsync(url).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("iOSFakeRun/0.9");

        return client;
    }

    private static string ResolveCacheRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "iOSFakeRun",
            "MapCache");

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception)
        {
            root = Path.Combine(Path.GetTempPath(), "iOSFakeRun-MapCache");
        }

        return root;
    }
}

/// <summary>
/// A self-contained slippy map: raster tiles, the planned route, the track the simulator actually
/// produced, and the current position. Everything is drawn straight from the render pass instead of
/// through child elements, which keeps panning and zooming cheap.
/// </summary>
public sealed class MapCanvas : FrameworkElement
{
    /// <summary>Room left around the route when fitting it to the viewport, in device pixels.</summary>
    private const double FitMarginPixels = 36.0;

    /// <summary>Cap on retained track points, a little over an hour of running at one per second.</summary>
    private const int MaxTrailPoints = 4096;

    /// <summary>Points needed before the heading arrow is trustworthy, since heading is smoothed in.</summary>
    private const int HeadingWarmupPoints = 3;

    private const double MarkerRadius = 5.0;

    private static readonly Brush BackdropBrush = FrozenBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
    private static readonly Brush MarkerBrush = FrozenBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly Brush MarkerRingBrush = FrozenBrush(Colors.White);
    private static readonly Brush EndpointBrush = FrozenBrush(Colors.White);
    private static readonly Brush ScaleBarBrush = FrozenBrush(Color.FromArgb(0xE6, 0x18, 0x2A, 0x3A));
    private static readonly Brush HintBrush = FrozenBrush(Color.FromArgb(0x99, 0x30, 0x38, 0x40));

    private static readonly Pen RouteCasingPen = FrozenPen(Color.FromArgb(0xB4, 0x0D, 0x22, 0x38), 7.0);
    private static readonly Pen RoutePen = FrozenPen(Color.FromRgb(0x42, 0xA5, 0xF5), 3.5);

    /// <summary>
    /// One colour per lap, cycling. A single colour turns a multi-lap run into one line drawn over
    /// itself again and again, which hides the only thing worth looking at in it: how the laps differ.
    /// The hues stay away from the route's blue so the two traces never get confused.
    /// </summary>
    private static readonly Pen[] TrailPens =
    {
        FrozenPen(Color.FromArgb(0xE6, 0xFF, 0x8F, 0x00), 2.0),
        FrozenPen(Color.FromArgb(0xE6, 0xE9, 0x1E, 0x63), 2.0),
        FrozenPen(Color.FromArgb(0xE6, 0x43, 0xA0, 0x47), 2.0),
        FrozenPen(Color.FromArgb(0xE6, 0x8E, 0x24, 0xAA), 2.0),
        FrozenPen(Color.FromArgb(0xE6, 0xF4, 0x51, 0x1E), 2.0),
        FrozenPen(Color.FromArgb(0xE6, 0x00, 0x89, 0x7B), 2.0)
    };

    private static readonly Pen MarkerRingPen = FrozenPen(Color.FromArgb(0x66, 0x00, 0x00, 0x00), 1.0);
    private static readonly Pen MarkerArrowPen = FrozenPen(Colors.White, 1.0);
    private static readonly Pen EndpointPen = FrozenPen(Color.FromRgb(0x0D, 0x22, 0x38), 1.5);
    private static readonly Pen ScaleBarPen = FrozenPen(Color.FromArgb(0xE6, 0x18, 0x2A, 0x3A), 1.5);

    private static readonly Typeface ScaleBarTypeface = new("Microsoft YaHei UI");
    private static readonly Typeface HintTypeface = new("FangSong");

    /// <summary>One sample of the track that was sent to the device, tagged with the lap it belongs to.</summary>
    private readonly struct TrailPoint
    {
        public TrailPoint(double[] position, int lap)
        {
            Position = position;
            Lap = lap;
        }

        public double[] Position { get; }

        public int Lap { get; }
    }

    private readonly TileStore _tiles;
    private readonly List<double[]> _route = new();
    private readonly List<TrailPoint> _trail = new();

    private MapTileSource _source = MapTileSources.All[0];
    private double[]? _position;
    private double _heading;
    private int _zoom = 12;
    private (double X, double Y) _center;
    private double _routeSignature = double.NaN;
    private bool _fitPending;

    // Zoom-0 world coordinates of the route and trail points, in the datum of the current tile
    // source. The conversion is view-independent, so it is cached per point and the render pass
    // only scales and offsets — which keeps drawing thousands of points free of per-frame
    // allocations. Bumped whenever the datum or the point set changes.
    private int _worldGeneration;
    private readonly List<(double X, double Y)> _routeWorld = new();
    private int _routeWorldGeneration = -1;
    private readonly List<(double X, double Y)> _trailWorld = new();
    private int _trailWorldGeneration = -1;

    private bool _panning;
    private Point _panPointer;
    private (double X, double Y) _panCenter;

    public MapCanvas()
    {
        _tiles = new TileStore(Dispatcher);
        _tiles.Changed += OnTilesChanged;

        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        _center = WebMercator.ToWorld(35.0, 105.0, _zoom);
    }

    /// <summary>True once the map has enough context to be worth drawing tiles for.</summary>
    private bool HasContent => _route.Count > 0 || _position != null;

    /// <summary>Raised when panning turns following off, so the checkbox can follow along.</summary>
    internal event Action<bool>? FollowChanged;

    internal MapTileSource Source
    {
        get => _source;
        set
        {
            if (value == null || ReferenceEquals(value, _source))
            {
                return;
            }

            _source = value;
            _zoom = Math.Max(WebMercator.MinZoom, Math.Min(_zoom, _source.MaxZoom));

            // The cached projections were computed for the previous source's datum.
            _worldGeneration++;
            ClampCenter();
            InvalidateVisual();
        }
    }

    internal bool FollowPosition { get; set; } = true;

    /// <summary>
    /// Replaces the displayed route. Points are WGS-84, as everything else in the app is.
    /// Returns whether the route actually changed.
    /// </summary>
    internal bool SetRoute(IReadOnlyList<double[]>? route)
    {
        var incoming = route ?? Array.Empty<double[]>();
        var signature = RouteSignature(incoming);

        // Keystrokes that leave the route alone must not throw away the user's pan and zoom.
        if (signature.Equals(_routeSignature))
        {
            return false;
        }

        _routeSignature = signature;
        _route.Clear();
        _route.AddRange(incoming);
        _worldGeneration++;

        // The track was laid down against whatever route was on screen while it was being recorded.
        // Once the route changes it is a record of something else, and drawing it over this one
        // asserts a relationship between the two that does not exist.
        ClearTrail();

        _fitPending = _route.Count > 1;
        InvalidateVisual();

        return true;
    }

    internal void ClearTrail()
    {
        _trail.Clear();
        _trailWorld.Clear();
        _trailWorldGeneration = _worldGeneration;
        _position = null;
        _fitPending = _route.Count > 1;
        InvalidateVisual();
    }

    /// <summary>Adds one point to the track that was really sent to the device.</summary>
    internal void AppendTrail(double[] position, int lap)
    {
        if (position == null)
        {
            return;
        }

        if (_trail.Count > 0)
        {
            var last = _trail[^1].Position;

            // Snapshots repeat while paused; they must not stack up on one spot.
            if (Math.Abs(last[0] - position[0]) < 1e-7 && Math.Abs(last[1] - position[1]) < 1e-7)
            {
                return;
            }
        }

        _trail.Add(new TrailPoint(position, lap));

        // Keeps the projection cache index-aligned. When the cache is stale anyway the value is
        // recomputed with everything else on the next render, so this cannot go wrong.
        _trailWorld.Add(ProjectToWorld(position));

        if (_trail.Count > MaxTrailPoints)
        {
            var excess = _trail.Count - MaxTrailPoints;
            _trail.RemoveRange(0, excess);
            _trailWorld.RemoveRange(0, excess);
        }
    }

    internal void SetPosition(double[]? position, double heading)
    {
        _position = position;
        _heading = heading;

        if (FollowPosition)
        {
            RecentreOnPosition();
        }
        else
        {
            InvalidateVisual();
        }
    }

    /// <summary>Puts the current position back in the middle of the viewport.</summary>
    internal void RecentreOnPosition()
    {
        if (_position == null || ActualWidth <= 1.0 || ActualHeight <= 1.0)
        {
            return;
        }

        var (latitude, longitude) = ToDatum(_position);
        _center = WebMercator.ToWorld(latitude, longitude, _zoom);
        ClampCenter();
        InvalidateVisual();
    }

    /// <summary>Frames the whole route. Applied on the next render, once the size is known.</summary>
    internal void FitToRoute()
    {
        _fitPending = true;
        InvalidateVisual();
    }

    internal void ZoomIn()
    {
        ZoomBy(1, null);
    }

    internal void ZoomOut()
    {
        ZoomBy(-1, null);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Take whatever the layout offers; a zero desired size would collapse the map away.
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0.0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0.0 : availableSize.Height);
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 1.0 || height <= 1.0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        dpi = dpi <= 0.0 ? 1.0 : dpi;

        EnsureWorldCaches();

        if (_fitPending)
        {
            _fitPending = false;
            ComputeRouteFit(width, height, dpi);
        }

        context.DrawRectangle(BackdropBrush, null, new Rect(0.0, 0.0, width, height));

        var originX = _center.X - width * dpi / 2.0;
        var originY = _center.Y - height * dpi / 2.0;

        if (HasContent)
        {
            DrawTiles(context, originX, originY, width * dpi, height * dpi, dpi);
        }

        DrawRoute(context, originX, originY, dpi);
        DrawTrail(context, originX, originY, dpi);
        DrawMarker(context, originX, originY, dpi);

        if (HasContent)
        {
            DrawScaleBar(context, width, height, dpi);
        }
        else
        {
            DrawHint(context, width, height, dpi);
        }
    }

    #region 绘制

    private void DrawTiles(DrawingContext context, double originX, double originY, double visibleWidth,
        double visibleHeight, double dpi)
    {
        const int tileSize = WebMercator.TileSize;

        var firstX = (int)Math.Floor(originX / tileSize);
        var lastX = (int)Math.Floor((originX + visibleWidth) / tileSize);
        var firstY = (int)Math.Floor(originY / tileSize);
        var lastY = (int)Math.Floor((originY + visibleHeight) / tileSize);

        var tileCount = 1 << _zoom;
        var scale = tileSize / dpi;
        var overlay = _source.HasOverlay;

        for (var x = firstX; x <= lastX; x++)
        {
            // Columns wrap around the antimeridian so the projection stays in range on either side.
            var wrappedX = ((x % tileCount) + tileCount) % tileCount;

            for (var y = firstY; y <= lastY; y++)
            {
                if (y < 0 || y >= tileCount)
                {
                    continue;
                }

                var rect = new Rect((x * tileSize - originX) / dpi, (y * tileSize - originY) / dpi, scale, scale);

                var baseTile = _tiles.TryGet(_source, false, _zoom, wrappedX, y);

                if (baseTile != null)
                {
                    context.DrawImage(baseTile, rect);
                }

                if (!overlay)
                {
                    continue;
                }

                var overlayTile = _tiles.TryGet(_source, true, _zoom, wrappedX, y);

                if (overlayTile != null)
                {
                    context.DrawImage(overlayTile, rect);
                }
            }
        }
    }

    private void DrawRoute(DrawingContext context, double originX, double originY, double dpi)
    {
        if (_route.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();

        using (var stream = geometry.Open())
        {
            stream.BeginFigure(WorldToScreen(_routeWorld[0], originX, originY, dpi), false, false);

            for (var i = 1; i < _route.Count; i++)
            {
                stream.LineTo(WorldToScreen(_routeWorld[i], originX, originY, dpi), true, false);
            }
        }

        geometry.Freeze();

        // A dark casing under a bright stroke stays readable over both imagery and pale street tiles.
        context.DrawGeometry(null, RouteCasingPen, geometry);
        context.DrawGeometry(null, RoutePen, geometry);

        DrawEndpoint(context, _routeWorld[0], originX, originY, dpi);
        DrawEndpoint(context, _routeWorld[^1], originX, originY, dpi);
    }

    private void DrawEndpoint(DrawingContext context, (double X, double Y) world, double originX,
        double originY, double dpi)
    {
        const double half = 3.5;
        var screen = WorldToScreen(world, originX, originY, dpi);

        context.DrawRectangle(EndpointBrush, EndpointPen,
            new Rect(screen.X - half, screen.Y - half, half * 2.0, half * 2.0));
    }

    private void DrawTrail(DrawingContext context, double originX, double originY, double dpi)
    {
        if (_trail.Count < 2)
        {
            return;
        }

        // One geometry per palette entry rather than per lap: a long run laps a short route hundreds
        // of times and the colours repeat anyway, so this bounds the work at the palette size.
        for (var slot = 0; slot < TrailPens.Length; slot++)
        {
            var geometry = new StreamGeometry();
            var open = false;

            using (var stream = geometry.Open())
            {
                var previous = -1;

                for (var i = 0; i < _trail.Count; i++)
                {
                    if (_trail[i].Lap % TrailPens.Length != slot)
                    {
                        // Another lap owns this stretch; the next one of ours starts a new figure.
                        open = false;
                        previous = i;
                        continue;
                    }

                    if (previous >= 0)
                    {
                        if (!open)
                        {
                            stream.BeginFigure(WorldToScreen(_trailWorld[previous], originX, originY, dpi), false, false);
                            open = true;
                        }

                        stream.LineTo(WorldToScreen(_trailWorld[i], originX, originY, dpi), true, false);
                    }

                    previous = i;
                }
            }

            geometry.Freeze();
            context.DrawGeometry(null, TrailPens[slot], geometry);
        }
    }

    private void DrawMarker(DrawingContext context, double originX, double originY, double dpi)
    {
        if (_position == null)
        {
            return;
        }

        var screen = ToScreen(_position, originX, originY, dpi);

        // The heading only settles after a few steps, so it is withheld until it means something.
        if (_trail.Count >= HeadingWarmupPoints)
        {
            var arrow = new StreamGeometry();

            using (var stream = arrow.Open())
            {
                stream.BeginFigure(Polar(screen, _heading, 16.0), true, true);
                stream.LineTo(Polar(screen, _heading + 152.0, 8.5), true, false);
                stream.LineTo(Polar(screen, _heading - 152.0, 8.5), true, false);
            }

            arrow.Freeze();
            context.DrawGeometry(MarkerBrush, MarkerArrowPen, arrow);
        }

        context.DrawEllipse(MarkerRingBrush, MarkerRingPen, screen, MarkerRadius + 2.0, MarkerRadius + 2.0);
        context.DrawEllipse(MarkerBrush, null, screen, MarkerRadius, MarkerRadius);
    }

    private void DrawScaleBar(DrawingContext context, double width, double height, double dpi)
    {
        var (latitude, _) = WebMercator.ToLatLon(_center.X, _center.Y, _zoom);
        var metresPerPixel = WebMercator.MetresPerPixel(latitude, _zoom);

        if (metresPerPixel <= 0.0 || double.IsNaN(metresPerPixel))
        {
            return;
        }

        // The bar aims for a fifth of the width but snaps to a round distance.
        var metres = NiceDistance(width / 5.0 * metresPerPixel);
        var pixels = metres / metresPerPixel;

        const double left = 10.0;
        var bottom = height - 12.0;

        context.DrawLine(ScaleBarPen, new Point(left, bottom), new Point(left + pixels, bottom));
        context.DrawLine(ScaleBarPen, new Point(left, bottom - 4.0), new Point(left, bottom + 2.0));
        context.DrawLine(ScaleBarPen, new Point(left + pixels, bottom - 4.0), new Point(left + pixels, bottom + 2.0));

        var label = metres >= 1000.0
            ? $"{metres / 1000.0:0.#} km"
            : $"{metres:0} m";

        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            ScaleBarTypeface, 11.0, ScaleBarBrush, dpi);

        context.DrawText(text, new Point(left, bottom - 20.0));
    }

    private void DrawHint(DrawingContext context, double width, double height, double dpi)
    {
        var text = new FormattedText("粘贴路线坐标后在此显示地图", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, HintTypeface, 13.0, HintBrush, dpi);

        context.DrawText(text, new Point((width - text.Width) / 2.0, (height - text.Height) / 2.0));
    }

    #endregion

    #region 坐标与视图

    private Point ToScreen(double[] wgs, double originX, double originY, double dpi)
    {
        var (latitude, longitude) = ToDatum(wgs);
        var (worldX, worldY) = WebMercator.ToWorld(latitude, longitude, _zoom);

        return new Point((worldX - originX) / dpi, (worldY - originY) / dpi);
    }

    /// <summary>
    /// Brings the view-independent projections up to date. Called from the render pass; the work is
    /// one conversion per point and only happens after the route or the map source changed.
    /// </summary>
    private void EnsureWorldCaches()
    {
        if (_routeWorldGeneration != _worldGeneration)
        {
            _routeWorld.Clear();

            foreach (var point in _route)
            {
                _routeWorld.Add(ProjectToWorld(point));
            }

            _routeWorldGeneration = _worldGeneration;
        }

        if (_trailWorldGeneration != _worldGeneration)
        {
            _trailWorld.Clear();

            foreach (var trailPoint in _trail)
            {
                _trailWorld.Add(ProjectToWorld(trailPoint.Position));
            }

            _trailWorldGeneration = _worldGeneration;
        }
    }

    /// <summary>Zoom-0 world position of a WGS-84 point, in the current source's datum.</summary>
    private (double X, double Y) ProjectToWorld(double[] wgs)
    {
        var (latitude, longitude) = ToDatum(wgs);
        return WebMercator.ToWorld(latitude, longitude, 0);
    }

    /// <summary>
    /// Scales a cached zoom-0 world position into screen pixels. ToWorld is linear in the zoom
    /// factor, so the cached value only ever needs multiplying by 2^zoom and offsetting.
    /// </summary>
    private Point WorldToScreen((double X, double Y) world, double originX, double originY, double dpi)
    {
        var scale = (double)(1L << _zoom);

        return new Point((world.X * scale - originX) / dpi, (world.Y * scale - originY) / dpi);
    }

    /// <summary>
    /// Moves a WGS-84 point into the datum the current tile set is drawn in. On a GCJ-02 source this
    /// is what keeps the track from sitting half a kilometre away from its own street.
    /// </summary>
    private (double Latitude, double Longitude) ToDatum(double[] wgs)
    {
        if (_source.Datum != MapDatum.Gcj02)
        {
            return (wgs[0], wgs[1]);
        }

        var gcj = CoordinateUtils.Wgs84ToGcj02(wgs[0], wgs[1]);
        return (gcj[0], gcj[1]);
    }

    private (double Latitude, double Longitude) FromDatum(double latitude, double longitude)
    {
        if (_source.Datum != MapDatum.Gcj02)
        {
            return (latitude, longitude);
        }

        var wgs = CoordinateUtils.Gcj02ToWgs84(latitude, longitude);
        return (wgs[0], wgs[1]);
    }

    private double[] ScreenToWgs84(Point screen, double dpi)
    {
        var originX = _center.X - ActualWidth * dpi / 2.0;
        var originY = _center.Y - ActualHeight * dpi / 2.0;

        var (latitude, longitude) = WebMercator.ToLatLon(originX + screen.X * dpi, originY + screen.Y * dpi, _zoom);
        var wgs = FromDatum(latitude, longitude);

        double[] point = {wgs.Latitude, wgs.Longitude};
        return point;
    }

    private void ComputeRouteFit(double width, double height, double dpi)
    {
        if (_routeWorld.Count == 0)
        {
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        // Zoom-0 world units: the span of the route there fixes the largest zoom that still fits.
        // The projections are already cached for exactly this purpose.
        foreach (var world in _routeWorld)
        {
            minX = Math.Min(minX, world.X);
            minY = Math.Min(minY, world.Y);
            maxX = Math.Max(maxX, world.X);
            maxY = Math.Max(maxY, world.Y);
        }

        var availableWidth = Math.Max(32.0, width * dpi - 2.0 * FitMarginPixels);
        var availableHeight = Math.Max(32.0, height * dpi - 2.0 * FitMarginPixels);

        var spanX = Math.Max(1e-9, maxX - minX);
        var spanY = Math.Max(1e-9, maxY - minY);

        var fit = Math.Min(availableWidth / spanX, availableHeight / spanY);
        var zoom = (int)Math.Floor(Math.Log(fit, 2.0));

        _zoom = Math.Max(WebMercator.MinZoom, Math.Min(_source.MaxZoom, zoom));

        var scale = (double)(1L << _zoom);
        _center = ((minX + maxX) / 2.0 * scale, (minY + maxY) / 2.0 * scale);
        ClampCenter();
    }

    private void ZoomBy(int delta, Point? anchor)
    {
        var target = Math.Max(WebMercator.MinZoom, Math.Min(_source.MaxZoom, _zoom + delta));

        if (target == _zoom)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        dpi = dpi <= 0.0 ? 1.0 : dpi;

        var focus = anchor ?? new Point(ActualWidth / 2.0, ActualHeight / 2.0);

        // Whatever sits under the anchor has to stay put while the scale changes.
        var pinned = ScreenToWgs84(focus, dpi);

        _zoom = target;

        var (latitude, longitude) = ToDatum(pinned);
        var (worldX, worldY) = WebMercator.ToWorld(latitude, longitude, _zoom);

        _center = (worldX - (focus.X - ActualWidth / 2.0) * dpi,
            worldY - (focus.Y - ActualHeight / 2.0) * dpi);

        ClampCenter();
        InvalidateVisual();
    }

    private void ClampCenter()
    {
        var scale = WebMercator.TileSize * (double)(1L << _zoom);

        _center = (Math.Max(0.0, Math.Min(scale, _center.X)),
            Math.Max(0.0, Math.Min(scale, _center.Y)));
    }

    private static Point Polar(Point origin, double bearingDegrees, double distance)
    {
        var radians = bearingDegrees * Math.PI / 180.0;

        return new Point(origin.X + Math.Sin(radians) * distance, origin.Y - Math.Cos(radians) * distance);
    }

    /// <summary>
    /// A cheap fingerprint of a route, used to tell a real edit from a keystroke that changed nothing.
    /// </summary>
    private static double RouteSignature(IReadOnlyList<double[]> route)
    {
        if (route.Count == 0)
        {
            return 0.0;
        }

        var sum = 0.0;

        foreach (var point in route)
        {
            sum += point[0] + point[1];
        }

        return route.Count * 1e6 + sum;
    }

    private static double NiceDistance(double metres)
    {
        if (metres <= 0.0 || double.IsNaN(metres) || double.IsInfinity(metres))
        {
            return 1.0;
        }

        var magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(metres)));

        foreach (var step in new[] {10.0, 5.0, 2.0, 1.0})
        {
            if (step * magnitude <= metres)
            {
                return step * magnitude;
            }
        }

        return magnitude;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();

        return pen;
    }

    #endregion

    #region 交互

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        _panning = true;
        _panPointer = e.GetPosition(this);
        _panCenter = _center;

        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_panning)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        dpi = dpi <= 0.0 ? 1.0 : dpi;

        var pointer = e.GetPosition(this);

        _center = (_panCenter.X - (pointer.X - _panPointer.X) * dpi,
            _panCenter.Y - (pointer.Y - _panPointer.Y) * dpi);

        ClampCenter();

        // Dragging means the user wants to look somewhere else, so following has to let go.
        if (FollowPosition)
        {
            FollowPosition = false;
            FollowChanged?.Invoke(false);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_panning)
        {
            EndPan();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndPan();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        ZoomBy(e.Delta > 0 ? 1 : -1, e.GetPosition(this));
        e.Handled = true;
    }

    private void EndPan()
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Cursor = Cursors.Arrow;
    }

    private void OnTilesChanged()
    {
        InvalidateVisual();
    }

    #endregion
}
