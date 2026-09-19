using System;
using System.Collections.Generic;

namespace iOSFakeRun.FakeRun;

/// <summary>
/// A route converted into an arc-length parameterised, corner-smoothed polyline.
/// Positions are looked up by "metres travelled from the start", which is what keeps
/// the achieved pace independent of how the original vertices happened to be spaced.
/// </summary>
internal sealed class RunPath
{
    /// <summary>Spacing of the internal geometry. Fine enough that linear lookup error is negligible.</summary>
    private const double ResampleStepMetres = 1.0;

    private const double DuplicateToleranceMetres = 0.05;

    /// <summary>A route whose ends are within this distance is treated as a closed loop.</summary>
    private const double ClosureToleranceMetres = 1.0;

    private readonly List<double[]> _points = new();
    private readonly List<double> _cumulative = new();

    public double Length { get; private set; }

    public bool IsClosed { get; private set; }

    public int SourceVertexCount { get; }

    /// <summary>Length of the raw vertex-to-vertex polyline, before rounding. Useful for sanity checks.</summary>
    public double SourceLength { get; private set; }

    public RunPath(IReadOnlyList<double[]> vertices, double cornerSmoothingMetres)
    {
        if (vertices == null)
        {
            throw new ArgumentNullException(nameof(vertices));
        }

        var raw = Clean(vertices);
        SourceVertexCount = raw.Count;

        if (raw.Count < 2)
        {
            throw new ArgumentException("路径至少需要两个不同的坐标点", nameof(vertices));
        }

        SourceLength = PolylineLength(raw, false);

        var dense = Resample(raw, null);
        if (dense.Count < 2)
        {
            throw new ArgumentException("路径长度过短", nameof(vertices));
        }

        var radius = Math.Max(0, (int)Math.Round(cornerSmoothingMetres / ResampleStepMetres));
        var smoothed = radius == 0 ? dense : Smooth(dense, radius, IsClosed);

        Build(smoothed);
    }

    /// <summary>Drops non-finite points and consecutive duplicates, and detects a closed loop.</summary>
    private List<double[]> Clean(IReadOnlyList<double[]> vertices)
    {
        var cleaned = new List<double[]>();

        foreach (var vertex in vertices)
        {
            if (vertex == null || vertex.Length < 2 ||
                double.IsNaN(vertex[0]) || double.IsNaN(vertex[1]) ||
                double.IsInfinity(vertex[0]) || double.IsInfinity(vertex[1]))
            {
                continue;
            }

            double[] point = {vertex[0], vertex[1]};

            if (cleaned.Count > 0 && CoordinateUtils.CalcDistance(cleaned[^1], point) < DuplicateToleranceMetres)
            {
                continue;
            }

            cleaned.Add(point);
        }

        if (cleaned.Count > 2 && CoordinateUtils.CalcDistance(cleaned[0], cleaned[^1]) < ClosureToleranceMetres)
        {
            IsClosed = true;
            cleaned.RemoveAt(cleaned.Count - 1);
        }

        return cleaned;
    }

    private static double PolylineLength(IReadOnlyList<double[]> points, bool closed)
    {
        var total = 0.0;

        for (var i = 1; i < points.Count; i++)
        {
            total += CoordinateUtils.CalcDistance(points[i - 1], points[i]);
        }

        if (closed && points.Count > 1)
        {
            total += CoordinateUtils.CalcDistance(points[^1], points[0]);
        }

        return total;
    }

    /// <summary>Walks the raw polyline at fixed arc-length intervals, optionally against a known total length.</summary>
    private List<double[]> Resample(IReadOnlyList<double[]> raw, double? knownLength)
    {
        var walk = new List<double[]>(raw);
        if (IsClosed)
        {
            // Close the ring so the resampler sweeps the final segment too; the duplicate seam is dropped below.
            double[] seam = {raw[0][0], raw[0][1]};
            walk.Add(seam);
        }

        var total = knownLength ?? PolylineLength(walk, false);
        var steps = (int)Math.Floor(total / ResampleStepMetres);

        var result = new List<double[]>();
        var segment = 1;
        var segmentStartDistance = 0.0;
        var segmentLength = CoordinateUtils.CalcDistance(walk[0], walk[1]);

        for (var step = 0; step <= steps; step++)
        {
            var target = step * ResampleStepMetres;

            while (segment < walk.Count - 1 && target > segmentStartDistance + segmentLength)
            {
                segmentStartDistance += segmentLength;
                segment++;
                segmentLength = CoordinateUtils.CalcDistance(walk[segment - 1], walk[segment]);
            }

            var t = segmentLength <= 0 ? 0.0 : (target - segmentStartDistance) / segmentLength;
            t = Math.Max(0.0, Math.Min(1.0, t));

            double[] point =
            {
                CoordinateUtils.Lerp(walk[segment - 1][0], walk[segment][0], t),
                CoordinateUtils.Lerp(walk[segment - 1][1], walk[segment][1], t)
            };

            result.Add(point);
        }

        // Always land exactly on the far end so the last partial step is not lost.
        var last = walk[^1];
        if (result.Count == 0 || CoordinateUtils.CalcDistance(result[^1], last) > DuplicateToleranceMetres)
        {
            double[] end = {last[0], last[1]};
            result.Add(end);
        }

        if (IsClosed && result.Count > 1 && CoordinateUtils.CalcDistance(result[0], result[^1]) < DuplicateToleranceMetres)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    /// <summary>Averaging pass over positions. This is what turns the original hard vertices into rounded corners.</summary>
    private static List<double[]> Smooth(List<double[]> points, int radius, bool wrap)
    {
        var current = points;

        for (var pass = 0; pass < 2; pass++)
        {
            var next = new List<double[]>(current.Count);
            var count = current.Count;

            for (var i = 0; i < count; i++)
            {
                var sumLatitude = 0.0;
                var sumLongitude = 0.0;
                var samples = 0;

                for (var offset = -radius; offset <= radius; offset++)
                {
                    var index = i + offset;

                    if (wrap)
                    {
                        index = ((index % count) + count) % count;
                    }
                    else
                    {
                        index = Math.Max(0, Math.Min(count - 1, index));
                    }

                    sumLatitude += current[index][0];
                    sumLongitude += current[index][1];
                    samples++;
                }

                double[] point = {sumLatitude / samples, sumLongitude / samples};
                next.Add(point);
            }

            current = next;
        }

        return current;
    }

    private void Build(List<double[]> points)
    {
        _points.Clear();
        _cumulative.Clear();

        _points.AddRange(points);

        if (IsClosed)
        {
            // Re-attach the seam so a lap of Length returns exactly to the start.
            double[] seam = {_points[0][0], _points[0][1]};
            _points.Add(seam);
        }

        _cumulative.Add(0.0);
        var total = 0.0;

        for (var i = 1; i < _points.Count; i++)
        {
            total += CoordinateUtils.CalcDistance(_points[i - 1], _points[i]);
            _cumulative.Add(total);
        }

        Length = total;
    }

    /// <summary>Position at a distance from the start, clamped into the path.</summary>
    public double[] PositionAt(double distanceFromStart)
    {
        if (distanceFromStart <= 0)
        {
            return _points[0];
        }

        if (distanceFromStart >= Length)
        {
            return _points[^1];
        }

        var index = UpperBound(_cumulative, distanceFromStart);
        var before = index - 1;
        var span = _cumulative[index] - _cumulative[before];
        var t = span <= 0 ? 0.0 : (distanceFromStart - _cumulative[before]) / span;

        double[] point =
        {
            CoordinateUtils.Lerp(_points[before][0], _points[index][0], t),
            CoordinateUtils.Lerp(_points[before][1], _points[index][1], t)
        };

        return point;
    }

    /// <summary>Position along the current lap, optionally traversed in reverse (out-and-back laps).</summary>
    public double[] PositionAtDirection(double distanceFromStart, bool reverse)
    {
        var clamped = Math.Max(0.0, Math.Min(Length, distanceFromStart));
        return PositionAt(reverse ? Length - clamped : clamped);
    }

    /// <summary>
    /// Heading over a lookahead window rather than between two adjacent samples.
    /// Using a window is what stops the perpendicular drift from flipping sign every second,
    /// which is what produced the zig-zag track in earlier attempts.
    /// </summary>
    public double HeadingAtDirection(double distanceFromStart, bool reverse, double lookaheadMetres)
    {
        var lookahead = Math.Min(lookaheadMetres, Math.Max(0.5, Length / 4.0));

        var here = PositionAtDirection(distanceFromStart, reverse);
        var ahead = PositionAtDirection(distanceFromStart + lookahead, reverse);

        if (CoordinateUtils.CalcDistance(here, ahead) < 0.1)
        {
            var behind = PositionAtDirection(Math.Max(0.0, distanceFromStart - lookahead), reverse);

            if (CoordinateUtils.CalcDistance(behind, here) < 0.1)
            {
                return 0.0;
            }

            here = behind;
        }

        return CoordinateUtils.CalculateBearing(here, ahead);
    }

    private static int UpperBound(List<double> values, double target)
    {
        var low = 0;
        var high = values.Count - 1;

        while (low < high)
        {
            var mid = (low + high) / 2;

            if (values[mid] <= target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
