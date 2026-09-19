using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace iOSFakeRun.FakeRun;

internal enum RunOutcome
{
    /// <summary>The target distance or lap count was reached.</summary>
    Completed,

    /// <summary>The user stopped the run.</summary>
    Stopped,

    /// <summary>The device stopped accepting positions.</summary>
    Failed
}

/// <summary>
/// The step-by-step run model: given a route and options, advances time and produces the next
/// position. Contains no threading, no sleeping and no device access, so it can be driven directly
/// by a test harness at full speed.
/// </summary>
internal sealed class RunSimulator
{
    private const double StopSpeedThreshold = 0.05;

    /// <summary>Speed-wobble correlation time.</summary>
    private const double SpeedNoiseTauSeconds = 20.0;

    /// <summary>
    /// Correlation time of the position error. A receiver does not resample its error from scratch
    /// every second; its error drifts. Treating it as independent per sample is what makes the
    /// coordinates measure longer than the route actually is, which shows up as a run that is
    /// faster than the speed it was set to.
    /// </summary>
    private const double PositionNoiseTauSeconds = 45.0;

    private const double HeadingLookaheadMetres = 5.0;

    /// <summary>Heading smoothing per step. A windowed, smoothed heading is what removes the zig-zag.</summary>
    private const double HeadingSmoothing = 0.35;

    private readonly RunOptions _options;
    private readonly RunPath _path;
    private readonly SpeedScheduler _scheduler;
    private readonly PathDriftController _drift;
    private readonly Random _random;

    private double _speed;
    private double _noise;
    private double _offsetNorth;
    private double _offsetEast;
    private double _heading;
    private double[]? _position;
    private double[]? _previous;
    private bool _rampDown;

    public RunSimulator(RunOptions options, RunPath path, Random? random = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _path = path ?? throw new ArgumentNullException(nameof(path));

        _random = random ?? new Random();
        _scheduler = new SpeedScheduler(options);
        _drift = new PathDriftController(options, _random);

        TotalLaps = options.Mode == RunMode.Laps ? Math.Max(1, options.LapCount) : 0;
        TotalDistance = options.Mode == RunMode.Distance
            ? Math.Max(1.0, options.TargetDistanceMetres)
            : _path.Length * Math.Max(1, TotalLaps);
    }

    public double TotalDistance { get; }

    public int TotalLaps { get; }

    public double Distance { get; private set; }

    public double MovingSeconds { get; private set; }

    /// <summary>Metres measured along the coordinate stream handed to the device.</summary>
    public double EmittedDistance { get; private set; }

    /// <summary>How far the last reported position strayed from the ideal route, in metres.</summary>
    public double Deviation { get; private set; }

    /// <summary>Widest that stray has been over the run so far.</summary>
    public double MaximumDeviation { get; private set; }

    public double Speed => _speed;

    public bool RampDown => _rampDown;

    public bool Finished { get; private set; }

    /// <summary>Widest the position may stray from the route, for sanity checks.</summary>
    public double MaximumOffsetFromRoute =>
        (_options.EnableDrift ? Math.Min(10.0, _options.DriftAmplitudeMetres) : 0.0) +
        (_options.EnableGpsNoise ? 3.0 * _options.GpsNoiseSigmaMetres : 0.0);

    /// <summary>Asks for a decelerating stop, the way a real run ends.</summary>
    public void RequestStop()
    {
        _rampDown = true;
    }

    /// <summary>
    /// Advances the model by <paramref name="dt"/> seconds.
    /// Returns the position to send, or null once the run has finished.
    /// </summary>
    public double[]? Advance(double dt)
    {
        if (Finished)
        {
            return null;
        }

        if (dt <= 0)
        {
            dt = 1.0;
        }

        MovingSeconds += dt;

        var target = _rampDown ? 0.0 : _scheduler.TargetAt(MovingSeconds);

        // Slew-rate limiting. This one line produces the start ramp, the end ramp and the smooth
        // hand-over between speed tiers, and it bounds acceleration at MaxAcceleration.
        _speed = Approach(_speed, target, _options.MaxAcceleration * dt);

        if (_rampDown && _speed < StopSpeedThreshold)
        {
            Finished = true;
            return null;
        }

        var stepSpeed = _speed;

        if (_options.EnableSpeedNoise && _options.SpeedNoiseFraction > 0)
        {
            _noise = AdvanceNoise(_noise, dt, _options.SpeedNoiseFraction, _random);
            stepSpeed = Math.Max(0.0, _speed * (1.0 + _noise));
        }

        Distance += stepSpeed * dt;

        if (!_rampDown && Distance >= TotalDistance)
        {
            _rampDown = true;
        }

        var lapLength = _path.Length;
        var lapIndex = lapLength > 0 ? (int)(Distance / lapLength) : 0;
        var withinLap = lapLength > 0 ? Distance % lapLength : 0.0;

        // A closed loop just keeps going; an open route is run out and back so laps join up instead
        // of jumping straight back to the start.
        var reverse = _options.AlternateLapDirection && !_path.IsClosed && lapIndex % 2 == 1;

        var position = _path.PositionAtDirection(withinLap, reverse);
        var rawHeading = _path.HeadingAtDirection(withinLap, reverse, HeadingLookaheadMetres);

        _heading = SmoothAngle(_heading, rawHeading, HeadingSmoothing);

        // Where the runner would be on a perfect receiver. Everything below moves away from it, so
        // keeping the reference lets the stray be measured rather than recomputed from the settings.
        var ideal = position;

        var offset = _drift.OffsetAt(MovingSeconds);

        if (Math.Abs(offset) > 0.001)
        {
            position = CoordinateUtils.CalculateDestination(position, _heading + 90.0, offset);
        }

        if (_options.EnableGpsNoise && _options.GpsNoiseSigmaMetres > 0)
        {
            var sigma = _options.GpsNoiseSigmaMetres;

            // A wandering offset that persists across samples, not a fresh draw each time.
            _offsetNorth = AdvanceNoise(_offsetNorth, dt, sigma, _random, PositionNoiseTauSeconds);
            _offsetEast = AdvanceNoise(_offsetEast, dt, sigma, _random, PositionNoiseTauSeconds);

            position = CoordinateUtils.OffsetByMetres(position, _offsetNorth, _offsetEast);
        }

        Deviation = CoordinateUtils.CalcDistance(ideal, position);
        MaximumDeviation = Math.Max(MaximumDeviation, Deviation);

        // Kept so a paused map still knows where the runner is standing.
        _position = position;

        // The model's arc length and the length of the track the device is handed are two different
        // numbers, and only the second one is what a fitness app measures. Nothing else tracks it,
        // so the gap stays invisible without this.
        if (_previous != null)
        {
            EmittedDistance += CoordinateUtils.CalcDistance(_previous, position);
        }

        _previous = position;

        return position;
    }

    public RunStatus Snapshot(double elapsedSeconds, bool paused)
    {
        var lap = _path.Length > 0 ? (int)(Distance / _path.Length) + 1 : 1;

        return new RunStatus
        {
            ElapsedSeconds = elapsedSeconds,
            MovingSeconds = MovingSeconds,
            DistanceMetres = Distance,
            EmittedDistanceMetres = EmittedDistance,
            TotalDistanceMetres = TotalDistance,
            CurrentSpeed = paused ? 0.0 : _speed,
            Lap = Math.Min(lap, TotalLaps > 0 ? TotalLaps : lap),
            TotalLaps = TotalLaps,
            Position = _position,
            Heading = _heading,
            DeviationMetres = Deviation,
            MaximumDeviationMetres = MaximumDeviation
        };
    }

    private static double Approach(double current, double target, double maxDelta)
    {
        var delta = target - current;

        if (Math.Abs(delta) <= maxDelta)
        {
            return target;
        }

        return current + Math.Sign(delta) * maxDelta;
    }

    /// <summary>
    /// Mean-reverting (Ornstein-Uhlenbeck) wobble, so a value drifts rather than jitters. The
    /// stationary standard deviation is <paramref name="sigma"/>, whatever the correlation time.
    /// </summary>
    private static double AdvanceNoise(double noise, double dt, double sigma, Random random,
        double tauSeconds = SpeedNoiseTauSeconds)
    {
        var decay = Math.Exp(-dt / tauSeconds);
        var variance = sigma * sigma * (1.0 - decay * decay);

        return noise * decay + Math.Sqrt(Math.Max(0.0, variance)) * Gaussian(random);
    }

    internal static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = 1.0 - random.NextDouble();

        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private static double SmoothAngle(double current, double target, double factor)
    {
        var delta = (target - current + 540.0) % 360.0 - 180.0;

        return (current + delta * factor + 360.0) % 360.0;
    }
}

/// <summary>
/// Owns the single thread that drives a run, and publishes snapshots to the UI.
/// Pause/stop/resume only touch volatile flags; all run state lives inside the simulator on its own thread.
/// </summary>
internal sealed class RunController
{
    /// <summary>The device expects roughly one update per second.</summary>
    private const double TickSeconds = 1.0;

    /// <summary>Sleep granularity, so pause and stop take effect promptly.</summary>
    private const double SleepSliceSeconds = 0.1;

    private readonly RunOptions _options;
    private readonly RunPath _path;
    private readonly ILocationSink _session;
    private readonly Action<RunStatus> _onStatus;
    private readonly Action<RunOutcome, string> _onFinished;
    private readonly object _stateLock = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _pauseRequested;
    private volatile bool _stopRequested;

    public RunController(RunOptions options, RunPath path, ILocationSink session, Action<RunStatus> onStatus, Action<RunOutcome, string> onFinished)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));
        _onFinished = onFinished ?? throw new ArgumentNullException(nameof(onFinished));
    }

    public bool IsRunning => _thread is { IsAlive: true };

    public bool IsPaused => _pauseRequested;

    /// <summary>
    /// Starts a run. Refuses rather than stacking a second loop on top of a live one — the upstream
    /// build spawned a new thread on every resume, which is what froze the process after repeated
    /// stop/start cycles.
    /// </summary>
    public bool Start(out string error)
    {
        error = string.Empty;

        lock (_stateLock)
        {
            if (_thread is { IsAlive: true })
            {
                _cts?.Cancel();

                if (!_thread.Join(TimeSpan.FromSeconds(3)))
                {
                    error = "上一次跑步的线程仍未结束，请稍后再试";
                    return false;
                }
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _pauseRequested = false;
            _stopRequested = false;

            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "FakeRunLoop"
            };

            _thread.Start();
            return true;
        }
    }

    public void Pause()
    {
        _pauseRequested = true;
    }

    public void Resume()
    {
        _pauseRequested = false;
    }

    /// <summary>Asks the loop to decelerate to a stop, the way a real run ends.</summary>
    public void Stop()
    {
        _stopRequested = true;
    }

    /// <summary>Cancels immediately, for application shutdown.</summary>
    public void RequestTermination()
    {
        _pauseRequested = false;
        _cts?.Cancel();

        var thread = _thread;

        if (thread is { IsAlive: true })
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void RunLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        var simulator = new RunSimulator(_options, _path);

        var clock = Stopwatch.StartNew();
        var lastTick = 0.0;
        var nextTick = TickSeconds;
        var failure = string.Empty;

        while (!token.IsCancellationRequested)
        {
            if (_pauseRequested)
            {
                // Hold position: the device keeps reporting the last coordinate it was given.
                Publish(simulator.Snapshot(clock.Elapsed.TotalSeconds, true));

                while (_pauseRequested && !token.IsCancellationRequested && !_stopRequested)
                {
                    Thread.Sleep(200);
                }

                var resumedAt = clock.Elapsed.TotalSeconds;
                lastTick = resumedAt;
                nextTick = resumedAt + TickSeconds;
                continue;
            }

            var now = clock.Elapsed.TotalSeconds;

            if (now < nextTick)
            {
                SleepSlices(Math.Min(nextTick - now, SleepSliceSeconds), token);
                continue;
            }

            var dt = now - lastTick;

            if (dt <= 0)
            {
                dt = TickSeconds;
            }
            else if (dt > 5.0)
            {
                // Recover from a stall (suspend, debugger) instead of teleporting along the route.
                dt = 5.0;
            }

            lastTick = now;
            nextTick += TickSeconds;

            if (nextTick < now)
            {
                nextTick = now + TickSeconds;
            }

            if (_stopRequested)
            {
                simulator.RequestStop();
            }

            var position = simulator.Advance(dt);

            if (position == null)
            {
                break;
            }

            if (!_session.Set(position[0], position[1]))
            {
                var recovered = RecoverAndRetry(simulator, clock, ref lastTick, ref nextTick, token, position);

                if (recovered)
                {
                    continue;
                }

                // A stop or cancellation during recovery ends the run as a stop, not a failure.
                if (_stopRequested || token.IsCancellationRequested)
                {
                    break;
                }

                failure = BuildFailureMessage();
                break;
            }

            Publish(simulator.Snapshot(clock.Elapsed.TotalSeconds, false));
        }

        _pauseRequested = false;

        var outcome = failure.Length > 0
            ? RunOutcome.Failed
            : token.IsCancellationRequested || _stopRequested
                ? RunOutcome.Stopped
                : RunOutcome.Completed;

        try
        {
            _onFinished(outcome, failure);
        }
        catch (Exception)
        {
            // The UI is allowed to be gone by the time a run ends.
        }
    }

    /// <summary>
    /// Keeps retrying the failed position for a grace window instead of ending the run. The Apple
    /// stack and the phone's connections die quietly while the app sits connected, and rebuilding
    /// them takes seconds; the session retries internally on every call. The run model does not
    /// advance while recovering — the runner stands still, like a pause — so a recovered run stays
    /// consistent with what the phone shows.
    /// </summary>
    /// <returns>True when the device took the position again.</returns>
    private bool RecoverAndRetry(RunSimulator simulator, Stopwatch clock, ref double lastTick, ref double nextTick,
        CancellationToken token, double[] position)
    {
        var failedAt = clock.Elapsed.TotalSeconds;
        var attempt = 1;

        while (!_stopRequested && !token.IsCancellationRequested)
        {
            if (clock.Elapsed.TotalSeconds - failedAt >= _options.LocationRetryWindowSeconds)
            {
                return false;
            }

            PublishWithRecovery(simulator, clock.Elapsed.TotalSeconds, attempt);

            Thread.Sleep(1000);

            if (_session.Set(position[0], position[1]))
            {
                // No catch-up: the seconds spent recovering were spent standing still, not running.
                lastTick = clock.Elapsed.TotalSeconds;
                nextTick = lastTick + TickSeconds;
                return true;
            }

            attempt++;
        }

        return false;
    }

    private void PublishWithRecovery(RunSimulator simulator, double elapsedSeconds, int attempt)
    {
        var status = simulator.Snapshot(elapsedSeconds, paused: true);
        status.RecoveringAttempts = attempt;
        Publish(status);
    }

    private string BuildFailureMessage()
    {
        var stage = _session.LastFailure;

        if (stage.Length == 0)
        {
            stage = "定位服务无响应";
        }

        return $"修改定位失败（已自动重试 {_options.LocationRetryWindowSeconds:0} 秒）\n" +
               stage + "\n" +
               "建议：重新插拔手机数据线，点「重新检测」后再试\n" +
               "若反复出现：重启本程序，必要时重启手机";
    }

    private void SleepSlices(double seconds, CancellationToken token)
    {
        var remaining = seconds;

        // Deliberately does not watch _stopRequested: the ramp-down still needs to sleep between ticks.
        while (remaining > 0 && !token.IsCancellationRequested && !_pauseRequested)
        {
            var slice = Math.Min(SleepSliceSeconds, remaining);
            Thread.Sleep((int)Math.Round(slice * 1000));
            remaining -= slice;
        }
    }

    private void Publish(RunStatus status)
    {
        try
        {
            _onStatus(status);
        }
        catch (Exception)
        {
            // Status updates are best-effort; a closing window must not kill the loop.
        }
    }
}

/// <summary>
/// Turns the configured speed tiers into an instantaneous target speed.
/// Tiers are visited in ping-pong order, so a tier change is always to an adjacent tier.
/// </summary>
internal sealed class SpeedScheduler
{
    private const double MinSpeed = 0.0;
    private const double MaxSpeed = 30.0;

    private readonly RunOptions _options;
    private readonly List<int> _order = new();
    private readonly List<double> _boundaries = new();
    private readonly double _cycleSeconds;

    public SpeedScheduler(RunOptions options)
    {
        _options = options;

        if (!options.UseSpeedTiers || options.Tiers.Count == 0)
        {
            return;
        }

        var count = options.Tiers.Count;

        for (var i = 0; i < count; i++)
        {
            _order.Add(i);
        }

        for (var i = count - 2; i >= 1; i--)
        {
            _order.Add(i);
        }

        var running = 0.0;

        foreach (var index in _order)
        {
            running += Math.Max(1, options.Tiers[index].DurationSeconds);
            _boundaries.Add(running);
        }

        _cycleSeconds = running;
    }

    /// <summary>Tier indices in visit order, for verification.</summary>
    public IReadOnlyList<int> VisitOrder => _order;

    public double TargetAt(double movingSeconds)
    {
        if (_order.Count == 0 || _cycleSeconds <= 0)
        {
            return Clamp(_options.ResolvedConstantSpeed);
        }

        var positionInCycle = movingSeconds % _cycleSeconds;

        for (var i = 0; i < _boundaries.Count; i++)
        {
            if (positionInCycle < _boundaries[i])
            {
                return TierSpeed(_order[i]);
            }
        }

        return TierSpeed(_order[^1]);
    }

    private double TierSpeed(int index)
    {
        return Clamp(_options.Tiers[index].Speed * _options.SpeedCalibration);
    }

    private static double Clamp(double speed)
    {
        return Math.Max(MinSpeed, Math.Min(MaxSpeed, speed));
    }
}

/// <summary>
/// Drifts the reported position perpendicular to the direction of travel, holds the offset, then
/// eases back to the route: the GPS wander a real watch records.
/// </summary>
internal sealed class PathDriftController
{
    private enum DriftState
    {
        Idle,
        Offsetting,
        Holding,
        Returning
    }

    private readonly RunOptions _options;
    private readonly Random _random;

    private DriftState _state = DriftState.Idle;
    private double _stateStart;
    private double _fromOffset;
    private double _targetOffset;
    private double _holdSeconds;

    public PathDriftController(RunOptions options, Random random)
    {
        _options = options;
        _random = random;
    }

    /// <param name="movingSeconds">Run time excluding pauses, so pausing does not advance the drift.</param>
    public double OffsetAt(double movingSeconds)
    {
        var amplitude = Math.Max(0.0, Math.Min(10.0, _options.DriftAmplitudeMetres));

        if (!_options.EnableDrift || amplitude <= 0.001)
        {
            return 0.0;
        }

        switch (_state)
        {
            case DriftState.Idle:
                Begin(movingSeconds, amplitude);
                goto case DriftState.Offsetting;

            case DriftState.Offsetting:
            {
                var progress = (movingSeconds - _stateStart) / TransitionSeconds();

                if (progress >= 1.0)
                {
                    _state = DriftState.Holding;
                    _stateStart = movingSeconds;
                    _fromOffset = _targetOffset;
                    return _targetOffset;
                }

                return CoordinateUtils.Lerp(_fromOffset, _targetOffset, CoordinateUtils.SmoothStep(progress));
            }

            case DriftState.Holding:
            {
                if (movingSeconds - _stateStart >= _holdSeconds)
                {
                    _state = DriftState.Returning;
                    _stateStart = movingSeconds;
                    _fromOffset = _targetOffset;
                    return _targetOffset;
                }

                // Start the wobble at zero so the hand-over from Offsetting stays continuous.
                var wobble = Math.Sin((movingSeconds - _stateStart) * 0.15) * 0.15 * amplitude;
                return _targetOffset + wobble;
            }

            case DriftState.Returning:
            {
                var progress = (movingSeconds - _stateStart) / TransitionSeconds();

                if (progress >= 1.0)
                {
                    _state = DriftState.Idle;
                    return 0.0;
                }

                return CoordinateUtils.Lerp(_fromOffset, 0.0, CoordinateUtils.SmoothStep(progress));
            }
        }

        return 0.0;
    }

    private void Begin(double now, double amplitude)
    {
        _targetOffset = (_random.NextDouble() * 2.0 - 1.0) * amplitude;
        _fromOffset = 0.0;
        _stateStart = now;

        var minimum = Math.Max(0.0, Math.Min(_options.DriftHoldMinSeconds, _options.DriftHoldMaxSeconds));
        var maximum = Math.Max(minimum, _options.DriftHoldMaxSeconds);
        _holdSeconds = minimum + _random.NextDouble() * (maximum - minimum);

        _state = DriftState.Offsetting;
    }

    private double TransitionSeconds()
    {
        return Math.Max(1.0, _options.DriftTransitionSeconds);
    }
}
