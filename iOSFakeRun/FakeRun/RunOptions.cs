using System.Collections.Generic;

namespace iOSFakeRun.FakeRun;

internal enum RunMode
{
    /// <summary>Repeat the route a fixed number of times.</summary>
    Laps,

    /// <summary>Keep moving until a target distance is covered.</summary>
    Distance
}

internal sealed class SpeedTier
{
    /// <summary>Target speed in m/s. Absolute value, not a multiplier.</summary>
    public double Speed { get; set; } = 3.0;

    /// <summary>How long this tier is held before moving to an adjacent tier.</summary>
    public int DurationSeconds { get; set; } = 60;
}

internal sealed class RunOptions
{
    /// <summary>
    /// How long the run loop keeps retrying a failed position write before declaring the run
    /// failed. The Apple stack and the phone's connections die quietly while the app sits idle,
    /// and rebuilding them takes seconds, so a retry window turns that from a dead run into a
    /// pause the user never notices.
    /// </summary>
    public double LocationRetryWindowSeconds { get; set; } = 90.0;

    public RunMode Mode { get; set; } = RunMode.Laps;

    public int LapCount { get; set; } = 9;

    public double TargetDistanceMetres { get; set; } = 5000.0;

    /// <summary>When positive in distance mode, overrides the speed profile with a constant pace.</summary>
    public double TargetDurationSeconds { get; set; }

    /// <summary>Runs every other lap in reverse so laps join up instead of jumping back to the start.</summary>
    public bool AlternateLapDirection { get; set; } = true;

    public bool UseSpeedTiers { get; set; }

    public double ConstantSpeed { get; set; } = 3.0;

    public List<SpeedTier> Tiers { get; set; } = new();

    /// <summary>Longitudinal acceleration ceiling. Recreational runners sit near 1.5 m/s².</summary>
    public double MaxAcceleration { get; set; } = 1.5;

    public bool EnableSpeedNoise { get; set; } = true;

    /// <summary>Relative amplitude of the slow speed wobble (0.03 = ±3%).</summary>
    public double SpeedNoiseFraction { get; set; } = 0.03;

    /// <summary>
    /// Multiplies the speed a run is asked to hold, so the pace a fitness app reads back can be
    /// trimmed to match. Nothing about the model needs it at the default settings; it exists for the
    /// gap between "the coordinates say X" and "the app on the phone says Y", which only the user
    /// can see. A target time is deliberately exempt: it is a promise about when the run ends.
    /// </summary>
    public double SpeedCalibration { get; set; } = 1.0;

    public bool EnableDrift { get; set; } = true;

    /// <summary>Lateral wander away from the route, in metres.</summary>
    public double DriftAmplitudeMetres { get; set; } = 1.5;

    public double DriftHoldMinSeconds { get; set; } = 60.0;

    public double DriftHoldMaxSeconds { get; set; } = 120.0;

    public double DriftTransitionSeconds { get; set; } = 15.0;

    public bool EnableGpsNoise { get; set; } = true;

    public double GpsNoiseSigmaMetres { get; set; } = 1.5;

    /// <summary>Corner rounding radius. Must be small relative to the route's turn spacing.</summary>
    public double CornerSmoothingMetres { get; set; } = 5.0;

    /// <summary>
    /// The flat speed to move at when no tier profile is in use. When the user gave a target
    /// distance and a target time, this is the pace that finishes the run on time.
    /// </summary>
    public double ResolvedConstantSpeed
    {
        get
        {
            if (Mode == RunMode.Distance && TargetDurationSeconds > 1.0 && TargetDistanceMetres > 0)
            {
                return TargetDistanceMetres / TargetDurationSeconds;
            }

            return CalibratedConstantSpeed;
        }
    }

    /// <summary>The constant-speed setting after calibration, for the pace preview.</summary>
    public double CalibratedConstantSpeed => ConstantSpeed * SpeedCalibration;

    /// <summary>The single speed the profile averages out to, used for previews.</summary>
    public double ResolvedAverageSpeed
    {
        get
        {
            if (UseSpeedTiers && Tiers.Count > 0)
            {
                var weighted = 0.0;
                var total = 0.0;

                foreach (var tier in Tiers)
                {
                    var duration = tier.DurationSeconds < 1 ? 1 : tier.DurationSeconds;
                    weighted += tier.Speed * duration;
                    total += duration;
                }

                return total > 0 ? weighted / total * SpeedCalibration : ResolvedConstantSpeed;
            }

            return ResolvedConstantSpeed;
        }
    }
}

/// <summary>A snapshot of run progress, delivered to the UI on every tick.</summary>
internal sealed class RunStatus
{
    public double ElapsedSeconds { get; set; }

    public double MovingSeconds { get; set; }

    public double DistanceMetres { get; set; }

    public double TotalDistanceMetres { get; set; }

    public double CurrentSpeed { get; set; }

    public int Lap { get; set; }

    public int TotalLaps { get; set; }

    /// <summary>The position last sent to the device, WGS-84. Null until the first step.</summary>
    public double[]? Position { get; set; }

    /// <summary>Direction of travel in degrees clockwise from north, for the map marker.</summary>
    public double Heading { get; set; }

    /// <summary>How far the last reported position strayed from the ideal route, in metres.</summary>
    public double DeviationMetres { get; set; }

    /// <summary>Widest that stray has been over the run so far.</summary>
    public double MaximumDeviationMetres { get; set; }

    public double Progress => TotalDistanceMetres <= 0
        ? 0.0
        : System.Math.Max(0.0, System.Math.Min(1.0, DistanceMetres / TotalDistanceMetres));

    public double AverageSpeed => MovingSeconds <= 1.0 ? 0.0 : DistanceMetres / MovingSeconds;

    /// <summary>
    /// The speed measured from the track the device was actually handed, rather than from the model's
    /// own arc length. This is the number a fitness app on the phone will show, and the only one worth
    /// comparing against the setting.
    /// </summary>
    public double DeviceSpeed => MovingSeconds <= 1.0 ? 0.0 : EmittedDistanceMetres / MovingSeconds;

    /// <summary>Total length of the coordinate stream sent to the device, in metres.</summary>
    public double EmittedDistanceMetres { get; set; }

    /// <summary>
    /// Non-zero while the run loop is retrying failed position writes (the value is the attempt
    /// number). The model is standing still during this time, like a pause.
    /// </summary>
    public int RecoveringAttempts { get; set; }

    /// <summary>Seconds per kilometre at the current speed, or 0 when standing still.</summary>
    public double CurrentPaceSecondsPerKm => CurrentSpeed <= 0.05 ? 0.0 : 1000.0 / CurrentSpeed;

    public double AveragePaceSecondsPerKm => AverageSpeed <= 0.05 ? 0.0 : 1000.0 / AverageSpeed;

    public static string FormatPace(double secondsPerKm)
    {
        if (secondsPerKm <= 0 || double.IsInfinity(secondsPerKm) || double.IsNaN(secondsPerKm))
        {
            return "--'--\"";
        }

        var total = (int)System.Math.Round(secondsPerKm);
        return $"{total / 60}'{total % 60:00}\"";
    }

    public static string FormatDuration(double seconds)
    {
        var total = (int)System.Math.Round(seconds);
        return $"{total / 3600:00}:{total % 3600 / 60:00}:{total % 60:00}";
    }
}
