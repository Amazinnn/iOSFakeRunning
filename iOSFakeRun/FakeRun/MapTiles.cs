using System;
using System.Collections.Generic;
using System.Globalization;

namespace iOSFakeRun.FakeRun;

/// <summary>The geodetic datum a tile set is drawn in.</summary>
internal enum MapDatum
{
    /// <summary>Tiles line up with raw GPS coordinates.</summary>
    Wgs84,

    /// <summary>Tiles carry the Chinese GCJ-02 shift, so WGS-84 coordinates must be shifted first.</summary>
    Gcj02
}

/// <summary>One raster tile layer: where to fetch tiles from, and which datum they are drawn in.</summary>
internal sealed class MapTileSource
{
    public MapTileSource(string key, string name, MapDatum datum, string urlTemplate, string[] subdomains,
        int maxZoom, string attribution, string? overlayTemplate = null)
    {
        Key = key;
        Name = name;
        Datum = datum;
        UrlTemplate = urlTemplate;
        Subdomains = subdomains;
        MaxZoom = maxZoom;
        Attribution = attribution;
        OverlayTemplate = overlayTemplate;
    }

    /// <summary>Stable ASCII id, also used as the on-disk cache folder name.</summary>
    public string Key { get; }

    public string Name { get; }

    public MapDatum Datum { get; }

    /// <summary>Tile URL carrying {s}, {x}, {y} and {z} placeholders.</summary>
    public string UrlTemplate { get; }

    public string[] Subdomains { get; }

    public int MaxZoom { get; }

    public string Attribution { get; }

    /// <summary>Optional transparent layer drawn over the base tiles, such as road labels on imagery.</summary>
    public string? OverlayTemplate { get; }

    public bool HasOverlay => !string.IsNullOrEmpty(OverlayTemplate);

    /// <summary>Expands a URL template for one tile, rotating through the subdomains by tile position.</summary>
    public string BuildUrl(string template, int zoom, int x, int y)
    {
        var subdomain = Subdomains.Length == 0
            ? string.Empty
            : Subdomains[(int)(((uint)x + (uint)y) % (uint)Subdomains.Length)];

        return template
            .Replace("{s}", subdomain)
            .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture))
            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
    }

    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// The built-in tile layers. Each was measured to answer without an API key from this machine;
/// OpenStreetMap and CartoDB did not respond here at all, so they are not offered.
/// </summary>
internal static class MapTileSources
{
    public static readonly IReadOnlyList<MapTileSource> All = new[]
    {
        new MapTileSource("amap-vector", "高德矢量", MapDatum.Gcj02,
            "https://webrd0{s}.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=8&x={x}&y={y}&z={z}",
            new[] {"1", "2", "3", "4"}, 18, "© 高德地图"),

        new MapTileSource("amap-satellite", "高德卫星", MapDatum.Gcj02,
            "https://webst0{s}.is.autonavi.com/appmaptile?style=6&x={x}&y={y}&z={z}",
            new[] {"1", "2", "3", "4"}, 18, "© 高德地图",
            "https://webst0{s}.is.autonavi.com/appmaptile?style=8&x={x}&y={y}&z={z}"),

        new MapTileSource("esri-imagery", "Esri 卫星", MapDatum.Wgs84,
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
            Array.Empty<string>(), 19, "© Esri"),

        new MapTileSource("esri-street", "Esri 街道", MapDatum.Wgs84,
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}",
            Array.Empty<string>(), 19, "© Esri")
    };

    /// <summary>Looks a source up by key, for restoring a saved choice.</summary>
    public static MapTileSource ByKey(string? key)
    {
        foreach (var source in All)
        {
            if (string.Equals(source.Key, key, StringComparison.Ordinal))
            {
                return source;
            }
        }

        return All[0];
    }
}

/// <summary>
/// Web Mercator, the projection every XYZ tile set uses. World coordinates are pixels at the given
/// zoom, measured from the north-west corner; one tile is 256 of them.
/// </summary>
internal static class WebMercator
{
    public const int TileSize = 256;

    public const int MinZoom = 1;

    /// <summary>Mercator runs away towards the poles; tiled maps stop here.</summary>
    public const double MaxLatitude = 85.05112878;

    private const double EquatorialCircumferenceMetres = 40075016.686;

    public static (double X, double Y) ToWorld(double latitude, double longitude, int zoom)
    {
        var scale = TileSize * (double)(1L << zoom);
        var clamped = Math.Max(-MaxLatitude, Math.Min(MaxLatitude, latitude));
        var radians = clamped * Math.PI / 180.0;

        var x = (longitude + 180.0) / 360.0 * scale;
        var y = (1.0 - Math.Log(Math.Tan(radians) + 1.0 / Math.Cos(radians)) / Math.PI) / 2.0 * scale;

        return (x, y);
    }

    public static (double Latitude, double Longitude) ToLatLon(double worldX, double worldY, int zoom)
    {
        var scale = TileSize * (double)(1L << zoom);

        var longitude = worldX / scale * 360.0 - 180.0;
        var latitude = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * worldY / scale))) * 180.0 / Math.PI;

        return (latitude, longitude);
    }

    /// <summary>Ground distance covered by one world pixel at the given latitude.</summary>
    public static double MetresPerPixel(double latitude, int zoom)
    {
        var atEquator = EquatorialCircumferenceMetres / (TileSize * (double)(1L << zoom));

        return atEquator * Math.Cos(Math.Max(-MaxLatitude, Math.Min(MaxLatitude, latitude)) * Math.PI / 180.0);
    }
}
