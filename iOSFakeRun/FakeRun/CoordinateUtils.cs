using System;
using System.Collections.Generic;

namespace iOSFakeRun.FakeRun;

internal static class CoordinateUtils
{
    private const double XPi = 3.14159265358979324 * 3000.0 / 180.0;
    private const double Pi = 3.1415926535897932384626;
    private const double A = 6378245.0;
    private const double E = 0.00669342162296594323;
    public const double EarthRadius = 6378137;

    /// <summary>Metres per degree of latitude, adequate at route scale.</summary>
    private const double MetresPerDegreeLatitude = 111320.0;

    /// <summary>BD-09 (Baidu) to GCJ-02, the datum Chinese map services publish.</summary>
    public static double[] Bd09ToGcj02(double latitude, double longitude)
    {
        var x = longitude - 0.0065;
        var y = latitude - 0.006;
        var z = Math.Sqrt(x * x + y * y) - 0.00002 * Math.Sin(y * XPi);
        var theta = Math.Atan2(y, x) - 0.000003 * Math.Cos(x * XPi);

        double[] gcj = {z * Math.Sin(theta), z * Math.Cos(theta)};
        return gcj;
    }

    public static double[] Bd09ToWgs84(double latitude, double longitude)
    {
        var gcj = Bd09ToGcj02(latitude, longitude);
        return Gcj02ToWgs84(gcj[0], gcj[1]);
    }

    /// <summary>The GCJ-02 shift for a point already expressed in GCJ-02, in degrees.</summary>
    private static double[] Gcj02Offset(double latitude, double longitude)
    {
        var dLatitude = TransformLatitude(latitude - 35.0, longitude - 105.0);
        var dLongitude = TransformLongitude(latitude - 35.0, longitude - 105.0);

        var radLatitude = latitude / 180.0 * Pi;
        var magic = Math.Sin(radLatitude);
        magic = 1 - E * magic * magic;
        var sqrtMagic = Math.Sqrt(magic);

        dLongitude = dLongitude * 180.0 / (A / sqrtMagic * Math.Cos(radLatitude) * Pi);
        dLatitude = dLatitude * 180.0 / (A * (1 - E) / (magic * sqrtMagic) * Pi);

        double[] offset = {dLatitude, dLongitude};
        return offset;
    }

    /// <summary>
    /// WGS-84 to GCJ-02, needed to plot GPS coordinates on Chinese tile sources. At these latitudes
    /// the shift is several hundred metres, so skipping it puts the track on the wrong street.
    /// </summary>
    public static double[] Wgs84ToGcj02(double latitude, double longitude)
    {
        var offset = Gcj02Offset(latitude, longitude);

        double[] gcj = {latitude + offset[0], longitude + offset[1]};
        return gcj;
    }

    /// <summary>
    /// GCJ-02 to WGS-84. The shift is only inverted approximately, leaving the result a metre or two
    /// out, which is an order of magnitude below the shift being removed.
    /// </summary>
    public static double[] Gcj02ToWgs84(double latitude, double longitude)
    {
        var offset = Gcj02Offset(latitude, longitude);

        double[] wgs = {latitude - offset[0], longitude - offset[1]};
        return wgs;
    }

    private static double TransformLatitude(double latitude, double longitude)
    {
        var ret = -100.0 + 2.0 * longitude + 3.0 * latitude + 0.2 * latitude * latitude + 0.1 * longitude * latitude + 0.2 * Math.Sqrt(Math.Abs(longitude));
        ret += (20.0 * Math.Sin(6.0 * longitude * Pi) + 20.0 * Math.Sin(2.0 * longitude * Pi)) * 2.0 / 3.0;
        ret += (20.0 * Math.Sin(latitude * Pi) + 40.0 * Math.Sin(latitude / 3.0 * Pi)) * 2.0 / 3.0;
        ret += (160.0 * Math.Sin(latitude / 12.0 * Pi) + 320 * Math.Sin(latitude * Pi / 30.0)) * 2.0 / 3.0;

        return ret;
    }

    private static double TransformLongitude(double latitude, double longitude)
    {
        var ret = 300.0 + longitude + 2.0 * latitude + 0.1 * longitude * longitude + 0.1 * longitude * latitude + 0.1 * Math.Sqrt(Math.Abs(longitude));
        ret += (20.0 * Math.Sin(6.0 * longitude * Pi) + 20.0 * Math.Sin(2.0 * longitude * Pi)) * 2.0 / 3.0;
        ret += (20.0 * Math.Sin(longitude * Pi) + 40.0 * Math.Sin(longitude / 3.0 * Pi)) * 2.0 / 3.0;
        ret += (150.0 * Math.Sin(longitude / 12.0 * Pi) + 300.0 * Math.Sin(longitude / 30.0 * Pi)) * 2.0 / 3.0;

        return ret;
    }

    public static double CalcDistance(double[] pointA, double[] pointB)
    {
        var radLatitudeA = Rad(pointA[0]);
        var radLongitudeA = Rad(pointA[1]);
        var radLatitudeB = Rad(pointB[0]);
        var radLongitudeB = Rad(pointB[1]);
        var a = radLatitudeA - radLatitudeB;
        var b = radLongitudeA - radLongitudeB;
        var distance = 2 * Math.Asin(Math.Sqrt(Math.Pow(Math.Sin(a / 2), 2) + Math.Cos(radLatitudeA) * Math.Cos(radLatitudeB) * Math.Pow(Math.Sin(b / 2), 2))) * EarthRadius;

        return distance;
    }

    /// <summary>Initial bearing from A to B, in degrees clockwise from north.</summary>
    public static double CalculateBearing(double[] pointA, double[] pointB)
    {
        var radLatitudeA = Rad(pointA[0]);
        var radLatitudeB = Rad(pointB[0]);
        var deltaLongitude = Rad(pointB[1] - pointA[1]);

        var y = Math.Sin(deltaLongitude) * Math.Cos(radLatitudeB);
        var x = Math.Cos(radLatitudeA) * Math.Sin(radLatitudeB) - Math.Sin(radLatitudeA) * Math.Cos(radLatitudeB) * Math.Cos(deltaLongitude);

        return (Deg(Math.Atan2(y, x)) + 360.0) % 360.0;
    }

    /// <summary>Point reached by travelling <paramref name="distanceMeters"/> from <paramref name="point"/> along <paramref name="bearingDegrees"/>.</summary>
    public static double[] CalculateDestination(double[] point, double bearingDegrees, double distanceMeters)
    {
        var radLatitude = Rad(point[0]);
        var radLongitude = Rad(point[1]);
        var radBearing = Rad(bearingDegrees);
        var angular = distanceMeters / EarthRadius;

        var latitude = Math.Asin(Math.Sin(radLatitude) * Math.Cos(angular) +
                                 Math.Cos(radLatitude) * Math.Sin(angular) * Math.Cos(radBearing));
        var longitude = radLongitude + Math.Atan2(Math.Sin(radBearing) * Math.Sin(angular) * Math.Cos(radLatitude),
            Math.Cos(angular) - Math.Sin(radLatitude) * Math.Sin(latitude));

        double[] destination = {Deg(latitude), Deg(longitude)};
        return destination;
    }

    /// <summary>Displaces a point by a metric offset given as north/east components.</summary>
    public static double[] OffsetByMetres(double[] point, double northMetres, double eastMetres)
    {
        var latitude = point[0] + northMetres / MetresPerDegreeLatitude;
        var longitudeScale = Math.Max(0.01, Math.Cos(Rad(point[0])));
        var longitude = point[1] + eastMetres / (MetresPerDegreeLatitude * longitudeScale);

        double[] displaced = {latitude, longitude};
        return displaced;
    }

    public static double Lerp(double a, double b, double t)
    {
        return a + (b - a) * t;
    }

    /// <summary>Smoothstep easing on the unit interval, clamped outside it.</summary>
    public static double SmoothStep(double t)
    {
        var clamped = Math.Max(0.0, Math.Min(1.0, t));
        return clamped * clamped * (3.0 - 2.0 * clamped);
    }

    private static double Rad(double d)
    {
        return d * Math.PI / 180d;
    }

    private static double Deg(double rad)
    {
        return rad * 180.0 / Math.PI;
    }
}
