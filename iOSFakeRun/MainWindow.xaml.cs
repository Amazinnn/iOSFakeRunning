using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;
using iOSFakeRun.FakeRun;
using Newtonsoft.Json.Linq;

namespace iOSFakeRun;

public partial class MainWindow
{
    /// <summary>
    /// The drift hold is drawn between the shortest hold the user set and this multiple of it. A
    /// fixed ratio keeps the wander from looking periodic without asking for two numbers.
    /// </summary>
    private const double DriftHoldSpread = 2.0;

    /// <summary>
    /// Deadline for the lockdown handshake. On a dead connection the native call never returns on
    /// its own; this bounds it wherever it is used, UI thread included.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly IiDeviceApi _ideviceInstance = LibiMobileDevice.Instance.iDevice;
    private readonly ILockdownApi _lockdownInstance = LibiMobileDevice.Instance.Lockdown;
    private readonly ObservableCollection<SpeedTier> _tiers = new();

    private iDeviceHandle? _idevice;
    private LockdownClientHandle? _lockdownClient;
    private LocationSession? _locationSession;
    private RunController? _runController;

    public MainWindow()
    {
        InitializeComponent();

        NativeLibraries.Load();

        DataGridSpeedTiers.ItemsSource = _tiers;
        LoadDefaultTiers();
        LoadSavedRoute();
        SetupMap();
        BindSections();

        ProbeAppleService(startIfNeeded: true);
    }

    #region 设备连接

    private void Link(object sender, RoutedEventArgs e)
    {
        DisconnectDevice();

        if (!AppleDeviceService.IsMuxListening())
        {
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                AppleDeviceService.EnsureRunning(out var startMessage);
                TextBlockAppleStatus.Text = "Apple 设备服务: " + AppleServiceState();
                TextBlockAppleDetail.Text = startMessage;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        var count = 0;
        _ideviceInstance.idevice_get_device_list(out var udids, ref count);

        if (udids.Count == 0)
        {
            MessageBox.Show("无设备连接\n请确认 iPhone 已用数据线连接并解锁");
            return;
        }

        _ideviceInstance.idevice_new(out _idevice, udids[0]).ThrowOnError();

        if (!TryHandshake(_idevice!, out _lockdownClient, out var handshakeError))
        {
            // On a timeout the handshake thread may still be inside native code with the device
            // handle, so forget the pair instead of disposing it.
            _idevice = null;
            _lockdownClient = null;
            MessageBox.Show(handshakeError);
            return;
        }

        if (!DeviceUtils.GetName(_lockdownClient, out var deviceName) ||
            !DeviceUtils.GetVersion(_lockdownClient, out var iosVersion))
        {
            DisconnectDevice();
            MessageBox.Show("读取设备信息失败");
            return;
        }

        if (!FakeRun.Image.MountImage(_idevice!, _lockdownClient, iosVersion, out var mountError))
        {
            DisconnectDevice();
            MessageBox.Show("挂载开发者镜像失败\n" + mountError);
            return;
        }

        _locationSession = LocationSession.Open(BuildDeviceHandles, out var sessionError);

        if (_locationSession == null)
        {
            DisconnectDevice();
            MessageBox.Show(sessionError);
            return;
        }

        TextBlockDevice.Text = $"设备: {deviceName} / iOS {iosVersion}";
        StatusBarTextBlock.Text = "成功连接";
        MessageBox.Show("成功连接");
    }

    /// <summary>
    /// Builds a fresh device handle pair. Passed to the location session, which calls it whenever a
    /// write has failed and the handles underneath it are suspects — the Apple stack dies quietly,
    /// so rebuilding is the difference between a dead run and a few seconds of silence.
    /// </summary>
    private bool BuildDeviceHandles(out iDeviceHandle? idevice, out LockdownClientHandle? lockdown, out string error)
    {
        idevice = null;
        lockdown = null;
        error = string.Empty;

        if (!AppleDeviceService.IsMuxListening() && !AppleDeviceService.EnsureRunning(out var serviceError))
        {
            error = "Apple 设备服务未在运行，且自动启动失败\n" + serviceError;
            return false;
        }

        var count = 0;
        _ideviceInstance.idevice_get_device_list(out var udids, ref count);

        if (udids.Count == 0)
        {
            error = "未发现 iPhone（可能已断开）\n请重新插拔数据线后重试";
            return false;
        }

        if (_ideviceInstance.idevice_new(out idevice, udids[0]) != iDeviceError.Success)
        {
            error = "无法访问设备";
            return false;
        }

        if (!TryHandshake(idevice!, out lockdown, out error))
        {
            // On a timeout the handshake thread may still be inside native code holding the device
            // handle, so forget the pair instead of disposing it. One leaked handle per failed
            // rebuild is harmless; a use-after-free is not.
            idevice = null;
            lockdown = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs the lockdown handshake on a deadline thread. On a wedged usbmux or a half-dead phone
    /// connection the native handshake neither succeeds nor errors — it spins forever, and it must
    /// not be allowed to hang whatever thread needed it (UI thread included).
    /// </summary>
    private bool TryHandshake(iDeviceHandle device, out LockdownClientHandle? client, out string error)
    {
        client = null;
        error = string.Empty;

        LockdownClientHandle? produced = null;
        var ok = false;
        using var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                ok = _lockdownInstance.lockdownd_client_new_with_handshake(device, out produced, "iOSFakeRun") ==
                     LockdownError.Success;
            }
            catch (Exception)
            {
                ok = false;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "iOSFakeRun lockdown handshake"
        };

        thread.Start();

        if (!finished.Wait(HandshakeTimeout))
        {
            error = "无法与手机建立会话（超时）\n请重新插拔数据线后重试";
            return false;
        }

        if (ok)
        {
            client = produced;
            return true;
        }

        error = "无法与手机建立会话\n请确认手机已解锁并重新插拔数据线";
        return false;
    }

    private void UnLink(object sender, RoutedEventArgs e)
    {
        DisconnectDevice();
        StatusBarTextBlock.Text = "成功断开连接";
        MessageBox.Show("成功断开连接");
    }

    private async void ResetLocation(object sender, RoutedEventArgs e)
    {
        if (_locationSession == null)
        {
            MessageBox.Show("请先连接设备");
            return;
        }

        // The reset rides the same bounded-but-slow write path a run uses — with a half-dead
        // connection it can take the better part of a minute, which must not be spent frozen on
        // the UI thread.
        var session = _locationSession;
        ButtonResetLocation.IsEnabled = false;
        StatusBarTextBlock.Text = "正在重置定位...";

        var ok = await Task.Run(session.Reset);

        SetRunState(RunUiState.Idle);

        if (ok)
        {
            // The phone is back on its real position, so the fake track is no longer a description of
            // anything. Leaving it on the map next to the numbers would claim a run that is now void.
            ClearRunRecord();
            StatusBarTextBlock.Text = "成功连接";
            MessageBox.Show("成功重置定位");
        }
        else
        {
            StatusBarTextBlock.Text = "定位重置失败";

            // The session has already retried on rebuilt handles; what is left is hardware-level
            // advice, not a guess about a cause.
            var stage = session.LastFailure;
            MessageBox.Show("重置定位失败"
                            + (stage.Length > 0 ? "\n" + stage : string.Empty)
                            + "\n建议：重新插拔手机数据线后点「重新检测」，再点「重置定位」");
        }
    }

    private void DisconnectDevice()
    {
        _runController?.RequestTermination();
        _runController = null;

        _locationSession?.Dispose();
        _locationSession = null;

        _lockdownClient?.Dispose();
        _lockdownClient = null;

        _idevice?.Dispose();
        _idevice = null;

        TextBlockDevice.Text = "设备: 未连接";
    }

    #endregion

    #region 跑步

    private void StartRun(object sender, RoutedEventArgs e)
    {
        if (_locationSession == null)
        {
            MessageBox.Show("请先连接设备");
            return;
        }

        if (!TryBuildPath(out var path, out var error))
        {
            MessageBox.Show(error);
            return;
        }

        var options = BuildRunOptions();

        if (options.Mode == RunMode.Laps && options.LapCount < 1)
        {
            MessageBox.Show("循环次数至少为 1");
            return;
        }

        if (options.UseSpeedTiers && options.Tiers.Count == 0)
        {
            MessageBox.Show("速度阶梯至少需要一级");
            return;
        }

        ProgressBarRun.Value = 0.0;
        ClearRunRecord();

        // Re-engage following: the last run may have been watched with it switched off, and a run
        // that scrolls off the edge because of a setting from half an hour ago looks broken.
        CheckBoxMapFollow.IsChecked = true;

        var controller = new RunController(options, path, _locationSession, OnRunStatus, OnRunFinished);

        if (!controller.Start(out var startError))
        {
            MessageBox.Show(startError);
            return;
        }

        _runController = controller;

        SetRunState(RunUiState.Running);
        StatusBarTextBlock.Text = "正在跑步中...";
    }

    private void PauseRun(object sender, RoutedEventArgs e)
    {
        _runController?.Pause();
        SetRunState(RunUiState.Paused);
        StatusBarTextBlock.Text = "已暂停";
    }

    private void ResumeRun(object sender, RoutedEventArgs e)
    {
        _runController?.Resume();
        SetRunState(RunUiState.Running);
        StatusBarTextBlock.Text = "正在跑步中...";
    }

    private void StopRun(object sender, RoutedEventArgs e)
    {
        _runController?.Stop();
        StatusBarTextBlock.Text = "正在停止...";
    }

    private void OnRunStatus(RunStatus status)
    {
        // BeginInvoke, never Invoke: the run thread must not block waiting on the UI thread.
        Dispatcher.BeginInvoke((Action)(() =>
        {
            ProgressBarRun.Value = status.Progress * ProgressBarRun.Maximum;

            // The write retry window is silent from the inside; say it out loud or the run looks
            // frozen to whoever is watching.
            StatusBarTextBlock.Text = status.RecoveringAttempts > 0
                ? $"定位重试中（第 {status.RecoveringAttempts} 次尝试）..."
                : "正在跑步中...";

            TextBlockDistance.Text =
                $"距离: {status.DistanceMetres / 1000.0:0.00} / {status.TotalDistanceMetres / 1000.0:0.00} km";

            TextBlockPace.Text =
                $"当前配速: {RunStatus.FormatPace(status.CurrentPaceSecondsPerKm)} ({status.CurrentSpeed:0.00} m/s)";

            TextBlockAveragePace.Text = $"平均配速: {RunStatus.FormatPace(status.AveragePaceSecondsPerKm)}";
            TextBlockDeviceSpeed.Text = status.DeviceSpeed > 0.05
                ? $"设备侧: {RunStatus.FormatPace(1000.0 / status.DeviceSpeed)} ({status.DeviceSpeed:0.00} m/s)"
                : "设备侧: --'--\"";
            TextBlockTime.Text = $"用时: {RunStatus.FormatDuration(status.ElapsedSeconds)}";

            TextBlockLap.Text = status.TotalLaps > 0
                ? $"圈数: {status.Lap}/{status.TotalLaps}"
                : $"已绕行: {status.Lap} 圈";

            if (status.Position != null)
            {
                Map.AppendTrail(status.Position, status.Lap);
                Map.SetPosition(status.Position, status.Heading);
            }

            TextBlockDeviation.Text = status.MovingSeconds > 1.0
                ? $"偏离 {status.DeviationMetres:0.0} m（最大 {status.MaximumDeviationMetres:0.0} m）"
                : "偏离 --";
        }));
    }

    private void OnRunFinished(RunOutcome outcome, string message)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            _runController = null;
            SetRunState(RunUiState.Idle);

            switch (outcome)
            {
                case RunOutcome.Completed:
                    StatusBarTextBlock.Text = "跑步完成";
                    break;

                case RunOutcome.Stopped:
                    StatusBarTextBlock.Text = "已停止";
                    break;

                default:
                    StatusBarTextBlock.Text = "已中断";
                    MessageBox.Show(message);
                    break;
            }
        }));
    }

    private enum RunUiState
    {
        Idle,
        Running,
        Paused
    }

    private void SetRunState(RunUiState state)
    {
        var idle = state == RunUiState.Idle;

        ButtonRun.Visibility = idle ? Visibility.Visible : Visibility.Hidden;
        ButtonPause.Visibility = state == RunUiState.Running ? Visibility.Visible : Visibility.Hidden;
        ButtonResume.Visibility = state == RunUiState.Paused ? Visibility.Visible : Visibility.Hidden;
        ButtonStop.Visibility = idle ? Visibility.Hidden : Visibility.Visible;

        // Parameters must not change mid-run; the run thread already captured its own copy.
        TabControlConfig.IsEnabled = idle;
        ButtonLink.IsEnabled = idle;
        ButtonUnlink.IsEnabled = idle;
        ButtonResetLocation.IsEnabled = idle;

        // The route is a parameter too. Editing it mid-run does not affect the run, but the map
        // follows the text box, so it would replace the line being run against and yank the view away
        // from the runner.
        TextBoxRoute.IsEnabled = idle;
        CheckBoxWgs84Input.IsEnabled = idle;

        // Clearing the track is about the run that produced it, so it belongs on the same side of
        // the line as the other run parameters, not with the view controls it sits among.
        ButtonMapClearTrail.IsEnabled = idle;
    }

    /// <summary>
    /// Drops the record of the last run: the track on the map and the numbers describing it. They
    /// are two views of one thing, so they are always cleared together and never one without the
    /// other.
    /// </summary>
    private void ClearRunRecord()
    {
        Map.ClearTrail();

        ProgressBarRun.Value = 0.0;
        TextBlockDistance.Text = "距离: 0.00 / 0.00 km";
        TextBlockPace.Text = "当前配速: --'--\"";
        TextBlockAveragePace.Text = "平均配速: --'--\"";
        TextBlockDeviceSpeed.Text = "设备侧: --'--\"";
        TextBlockTime.Text = "用时: 00:00:00";
        TextBlockLap.Text = "圈数: -/-";
        TextBlockDeviation.Text = "偏离 --";
    }

    private RunOptions BuildRunOptions()
    {
        return new RunOptions
        {
            Mode = RadioButtonModeDistance.IsChecked == true ? RunMode.Distance : RunMode.Laps,
            LapCount = IntegerUpDownRunTimes.Value ?? 9,
            TargetDistanceMetres = (DoubleUpDownDistanceKm.Value ?? 5.0) * 1000.0,

            // Zero unless "目标用时" is the chosen source. A stale value here would silently override
            // the speed picked below, which is exactly the overlap this layout exists to remove.
            TargetDurationSeconds = RadioButtonSpeedTargetTime.IsChecked == true
                ? (DoubleUpDownTargetMinutes.Value ?? 30.0) * 60.0
                : 0.0,

            UseSpeedTiers = RadioButtonSpeedTiers.IsChecked == true,
            ConstantSpeed = DoubleUpDownSpeed.Value ?? 3.0,
            SpeedCalibration = DoubleUpDownSpeedCalibration.Value ?? 1.0,
            Tiers = _tiers.Select(tier => new SpeedTier
            {
                Speed = tier.Speed,
                DurationSeconds = tier.DurationSeconds
            }).ToList(),
            MaxAcceleration = DoubleUpDownMaxAcceleration.Value ?? 1.5,
            EnableDrift = CheckBoxDrift.IsChecked == true,
            DriftAmplitudeMetres = DoubleUpDownDriftAmplitude.Value ?? 1.5,
            DriftHoldMinSeconds = (DoubleUpDownDriftHoldMin.Value ?? 1.0) * 60.0,
            DriftHoldMaxSeconds = (DoubleUpDownDriftHoldMin.Value ?? 1.0) * 60.0 * DriftHoldSpread,
            DriftTransitionSeconds = DoubleUpDownDriftTransition.Value ?? 15.0,
            EnableGpsNoise = CheckBoxGpsNoise.IsChecked == true,
            GpsNoiseSigmaMetres = DoubleUpDownGpsNoiseSigma.Value ?? 1.5,
            EnableSpeedNoise = CheckBoxSpeedNoise.IsChecked == true,
            SpeedNoiseFraction = (DoubleUpDownSpeedNoisePercent.Value ?? 3.0) / 100.0,
            CornerSmoothingMetres = DoubleUpDownCornerSmoothing.Value ?? 5.0
        };
    }

    #endregion

    #region 路线解析

    private bool TryBuildPath(out RunPath path, out string error)
    {
        path = null!;
        error = string.Empty;

        var pointText = TextBoxRoute.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(pointText))
        {
            error = "请先粘贴路线坐标";
            return false;
        }

        if (!TryParseRoute(pointText, out var points, out var parseError))
        {
            WriteErrorLog(pointText, parseError);
            error = "解析路线数据失败\n" + parseError +
                    "\n请将路线从网页复制到左侧文本框内并确保数据格式合法";
            return false;
        }

        if (points.Count < 2)
        {
            error = "路线至少需要两个坐标点";
            return false;
        }

        try
        {
            path = new RunPath(ToWgs84(points), DoubleUpDownCornerSmoothing.Value ?? 5.0);
        }
        catch (Exception exception)
        {
            error = "构建路线失败\n" + exception.Message;
            return false;
        }

        SaveRoute(pointText);
        return true;
    }

    /// <summary>
    /// The route picker emits Baidu BD-09 coordinates; the device needs WGS-84. Coordinates pasted
    /// from GPX or an international map are already WGS-84 and must not be shifted again.
    /// </summary>
    private List<double[]> ToWgs84(List<double[]> points)
    {
        return CheckBoxWgs84Input.IsChecked == true
            ? points
            : points.Select(point => CoordinateUtils.Bd09ToWgs84(point[0], point[1])).ToList();
    }

    private static bool TryParseRoute(string pointText, out List<double[]> points, out string error)
    {
        points = new List<double[]>();
        error = string.Empty;

        var text = pointText.Trim();

        if (!text.StartsWith("["))
        {
            text = "[" + text + "]";
        }

        JArray array;

        try
        {
            array = JArray.Parse(text);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        foreach (var position in array)
        {
            var latitudeToken = position.SelectToken("lat");
            var longitudeToken = position.SelectToken("lng");

            if (!TryReadNumber(latitudeToken, out var latitude) || !TryReadNumber(longitudeToken, out var longitude))
            {
                error = "存在缺少 lat / lng 的坐标点";
                return false;
            }

            double[] point = {latitude, longitude};
            points.Add(point);
        }

        return points.Count > 0;
    }

    /// <summary>
    /// Reads a coordinate without touching the ambient culture. The upstream build parsed with the
    /// current culture, which failed outright on Windows locales that use a comma decimal separator.
    /// </summary>
    private static bool TryReadNumber(JToken? token, out double value)
    {
        value = 0.0;

        if (token == null || token.Type == JTokenType.Null)
        {
            return false;
        }

        if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
        {
            value = token.Value<double>();
            return true;
        }

        return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void LoadSavedRoute()
    {
        try
        {
            var path = AppPaths.ResolveExistingFile(AppPaths.RouteSaveFileName);

            if (path == null)
            {
                return;
            }

            TextBoxRoute.Text = File.ReadAllText(path);
            RefreshRouteSummary();
        }
        catch (Exception)
        {
            // A missing or unreadable saved route is not worth interrupting startup for.
        }
    }

    private static void SaveRoute(string pointText)
    {
        try
        {
            File.WriteAllText(AppPaths.ResolveWritePath(AppPaths.RouteSaveFileName), pointText);
        }
        catch (Exception)
        {
            // Saving is a convenience; a failure must not abort the run.
        }
    }

    private void RefreshRouteSummary()
    {
        var text = TextBoxRoute.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            // An empty box is a deliberate clear, so the map follows it and shows the hint again.
            LabelRouteSummary.Content = "路线: -";
            LabelRouteSummary.ClearValue(Control.ForegroundProperty);
            ApplyRoute(null);
            return;
        }

        if (!TryParseRoute(text, out var points, out _) || points.Count < 2)
        {
            // While the text is being edited it is unparseable for a keystroke or two. Blanking the
            // map on the way past would make it flash on every character, so the last good route
            // stays on screen and the summary carries the complaint.
            LabelRouteSummary.Content = "路线: 格式有误（地图仍显示上一条）";
            LabelRouteSummary.Foreground = Brushes.Firebrick;
            return;
        }

        var converted = ToWgs84(points);
        var length = 0.0;

        for (var i = 1; i < converted.Count; i++)
        {
            length += CoordinateUtils.CalcDistance(converted[i - 1], converted[i]);
        }

        LabelRouteSummary.Content = $"路线: {points.Count} 个点, 单圈约 {length / 1000.0:0.00} km";
        LabelRouteSummary.ClearValue(Control.ForegroundProperty);

        ApplyRoute(converted);
    }

    /// <summary>
    /// Hands a new route to the map, and drops the previous run's record if the route really
    /// changed — the track was run against the old one and no longer means anything next to this one.
    /// </summary>
    private void ApplyRoute(List<double[]>? converted)
    {
        if (Map.SetRoute(converted))
        {
            ClearRunRecord();
        }
    }

    private void RouteDatumChanged(object sender, RoutedEventArgs e)
    {
        // The datum decides how the route is converted, so the map has to be rebuilt with it.
        RefreshRouteSummary();
        Map.FitToRoute();
    }

    private void RouteTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshRouteSummary();
    }

    private static void WriteErrorLog(string pointText, string detail)
    {
        try
        {
            using var writer = new StreamWriter(AppPaths.ResolveWritePath(AppPaths.ErrorLogFileName), false);
            writer.WriteLine("Time: " + DateTime.Now);
            writer.WriteLine("Windows Version: " + Environment.OSVersion);
            writer.WriteLine("Windows Language: " + CultureInfo.InstalledUICulture.Name);
            writer.WriteLine("Point Text: " + pointText);
            writer.WriteLine("Exception: " + detail);
        }
        catch (Exception)
        {
            // Logging is best-effort.
        }
    }

    #endregion

    #region 地图

    private void SetupMap()
    {
        foreach (var source in MapTileSources.All)
        {
            ComboBoxMapSource.Items.Add(source);
        }

        ComboBoxMapSource.SelectedIndex = 0;

        Map.Source = MapTileSources.All[0];
        TextBlockMapAttribution.Text = MapTileSources.All[0].Attribution;
        Map.FollowChanged += following => CheckBoxMapFollow.IsChecked = following;

        // The route is loaded before the window has a size, so the first fit has to wait for layout.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => Map.FitToRoute()));
    }

    private void MapSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboBoxMapSource.SelectedItem is not MapTileSource source)
        {
            return;
        }

        Map.Source = source;
        TextBlockMapAttribution.Text = source.Attribution;

        // A source in a different datum moves the route by hundreds of metres; reframe it.
        Map.FitToRoute();
    }

    private void MapFit(object sender, RoutedEventArgs e)
    {
        Map.FitToRoute();
    }

    private void MapFollowToggled(object sender, RoutedEventArgs e)
    {
        Map.FollowPosition = CheckBoxMapFollow.IsChecked == true;

        if (Map.FollowPosition)
        {
            Map.RecentreOnPosition();
        }
    }

    private void MapZoomIn(object sender, RoutedEventArgs e)
    {
        Map.ZoomIn();
    }

    private void MapZoomOut(object sender, RoutedEventArgs e)
    {
        Map.ZoomOut();
    }

    private void MapClearTrail(object sender, RoutedEventArgs e)
    {
        ClearRunRecord();
    }

    private void MapCacheClear(object sender, RoutedEventArgs e)
    {
        TileStore.ClearCache();
        RefreshMapCacheText();
    }

    private void RefreshMapCacheText()
    {
        var limitMb = TileStore.CacheLimitBytes / 1024 / 1024;
        var usedMb = TileStore.CacheBytes / 1024.0 / 1024.0;

        TextBlockMapCache.Text = usedMb < 0.05
            ? $"几乎为空（上限 {limitMb} MB）"
            : $"{usedMb:0.0} MB / 上限 {limitMb} MB";
    }

    /// <summary>Refreshes the cache readout when the device tab comes up; a stale number helps nobody.</summary>
    private void ConfigTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Inner combo boxes raise this too, so only the tab control's own change counts.
        if (ReferenceEquals(e.OriginalSource, TabControlConfig) && TabControlConfig.SelectedIndex == 2)
        {
            RefreshMapCacheText();
        }
    }

    #endregion

    #region 配置界面

    private void LoadDefaultTiers()
    {
        _tiers.Clear();
        _tiers.Add(new SpeedTier { Speed = 2.0, DurationSeconds = 30 });
        _tiers.Add(new SpeedTier { Speed = 3.0, DurationSeconds = 60 });
        _tiers.Add(new SpeedTier { Speed = 4.0, DurationSeconds = 30 });
    }

    /// <summary>
    /// Wires every "this toggle owns that group" relationship in one place. WPF disables a
    /// container's children along with the container, so each group only needs its own IsEnabled
    /// set; nothing has to touch the individual controls inside.
    /// </summary>
    private void BindSections()
    {
        BindSection(PanelLapsFields, RadioButtonModeLaps);
        BindSection(PanelDistanceFields, RadioButtonModeDistance);
        BindSection(PanelConstantSpeed, RadioButtonSpeedConstant);
        BindSection(PanelTargetTime, RadioButtonSpeedTargetTime);
        BindSection(PanelSpeedTiers, RadioButtonSpeedTiers);
        BindSection(PanelSpeedCalibration, RadioButtonSpeedConstant, RadioButtonSpeedTiers);
        BindSection(PanelDrift, CheckBoxDrift);
        BindSection(PanelSpeedNoise, CheckBoxSpeedNoise);
        BindSection(PanelGpsNoise, CheckBoxGpsNoise);

        RadioButtonModeLaps.Checked += (_, _) => UpdateTargetTimeAvailability();
        RadioButtonModeDistance.Checked += (_, _) => UpdateTargetTimeAvailability();
        UpdateTargetTimeAvailability();

        foreach (var input in new[]
                 {
                     DoubleUpDownSpeed, DoubleUpDownDistanceKm, DoubleUpDownTargetMinutes,
                     DoubleUpDownSpeedCalibration
                 })
        {
            input.ValueChanged += (_, _) => RefreshSpeedHints();
        }

        RefreshSpeedHints();
    }

    private static void BindSection(FrameworkElement section, params ToggleButton[] toggles)
    {
        void Sync()
        {
            section.IsEnabled = Array.Exists(toggles, toggle => toggle.IsChecked == true);
        }

        foreach (var toggle in toggles)
        {
            toggle.Checked += (_, _) => Sync();
            toggle.Unchecked += (_, _) => Sync();
        }

        Sync();
    }

    /// <summary>
    /// "目标用时" is the one speed source that needs something else to exist first: the engine
    /// derives its speed by dividing the distance target by the time, and it only does that in
    /// distance mode.
    /// </summary>
    private void UpdateTargetTimeAvailability()
    {
        var available = RadioButtonModeDistance.IsChecked == true;

        RadioButtonSpeedTargetTime.IsEnabled = available;

        if (!available && RadioButtonSpeedTargetTime.IsChecked == true)
        {
            RadioButtonSpeedConstant.IsChecked = true;
        }
    }

    /// <summary>
    /// Shows what the selected speed source works out to, so a derived or averaged speed is visible
    /// instead of being implied by another field.
    /// </summary>
    private void RefreshSpeedHints()
    {
        // Ask the options object, so the readouts can never disagree with what actually runs.
        var options = BuildRunOptions();

        TextBlockConstantSpeedHint.Text =
            "≈ " + RunStatus.FormatPace(1000.0 / options.CalibratedConstantSpeed) + " /km";

        var distance = DoubleUpDownDistanceKm.Value ?? 5.0;
        var minutes = DoubleUpDownTargetMinutes.Value ?? 30.0;

        TextBlockTargetTimeHint.Text = minutes > 0.0
            ? $"{distance:0.00} km ÷ {minutes:0} min → {distance * 1000.0 / (minutes * 60.0):0.00} m/s" +
              $"（{RunStatus.FormatPace(minutes * 60.0 / distance)} /km）"
            : "目标用时要大于 0";

        TextBlockTierHint.Text = options.Tiers.Count > 0
            ? $"平均 {options.ResolvedAverageSpeed:0.00} m/s" +
              $"（{RunStatus.FormatPace(1000.0 / options.ResolvedAverageSpeed)} /km）"
            : "还没有阶梯";

        TextBlockSpeedCalibrationHint.Text = options.SpeedCalibration.Equals(1.0)
            ? "不校准"
            : options.UseSpeedTiers
                ? $"阶梯平均 {options.ResolvedAverageSpeed:0.00} m/s"
                : $"设定 {options.ConstantSpeed:0.00} → {options.CalibratedConstantSpeed:0.00} m/s";
    }

    private void AddSpeedTier(object sender, RoutedEventArgs e)
    {
        var last = _tiers.Count > 0 ? _tiers[^1] : null;

        _tiers.Add(new SpeedTier
        {
            Speed = last?.Speed ?? 3.0,
            DurationSeconds = last?.DurationSeconds ?? 60
        });

        RefreshSpeedHints();
    }

    private void RemoveSpeedTier(object sender, RoutedEventArgs e)
    {
        if (DataGridSpeedTiers.SelectedItem is SpeedTier selected)
        {
            _tiers.Remove(selected);
        }

        RefreshSpeedHints();
    }

    private void ResetSpeedTiers(object sender, RoutedEventArgs e)
    {
        LoadDefaultTiers();
        RefreshSpeedHints();
    }

    #endregion

    #region Apple 设备服务

    private void AppleDetect(object sender, RoutedEventArgs e)
    {
        ProbeAppleService(startIfNeeded: false);
    }

    private void AppleStart(object sender, RoutedEventArgs e)
    {
        ProbeAppleService(startIfNeeded: true);
    }

    private void AppleStop(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            AppleDeviceService.Stop(out var message);
            TextBlockAppleStatus.Text = "Apple 设备服务: " + AppleServiceState();
            TextBlockAppleDetail.Text = message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// The status bar only has room for the state; the full description is shown on the device tab.
    /// </summary>
    private static string AppleServiceState()
    {
        return AppleDeviceService.IsMuxListening() ? "在运行" : "未运行";
    }

    /// <summary>Probes the Apple device stack off the UI thread; the mux probe alone can take a second.</summary>
    private void ProbeAppleService(bool startIfNeeded)
    {
        TextBlockAppleDetail.Text = startIfNeeded ? "正在确保服务运行..." : "检测中...";

        Task.Run(() =>
        {
            string detail;

            if (startIfNeeded)
            {
                AppleDeviceService.EnsureRunning(out detail);
            }
            else
            {
                detail = AppleDeviceService.Describe();
            }

            var status = AppleDeviceService.Describe();

            Dispatcher.BeginInvoke((Action)(() =>
            {
                TextBlockAppleStatus.Text = "Apple 设备服务: " + AppleServiceState();
                TextBlockAppleDetail.Text = detail;
            }));
        });
    }

    #endregion

    private void Quit(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void About(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("iOSFakeRunning\n版本: v1.0\n原作者: Myth\n\n" +
                        "基于 LGPL-2.1 开源项目 Mythologyli/iOSFakeRun 修改，本仓库按同协议继续开源\n" +
                        "https://github.com/Mythologyli/iOSFakeRun\n\n" +
                        "请勿将本工具用于任何非法用途");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _runController?.RequestTermination();
        _runController = null;

        // Leave the phone on its real position rather than stuck on the fake one. The reset rides
        // the bounded write path, which can take tens of seconds on a half-dead connection, so it
        // runs off the UI thread with a short grace period: when it cannot finish, the window
        // still closes and the phone recovers on its next reboot.
        var session = _locationSession;

        if (session != null)
        {
            var resetThread = new Thread(() => session.Reset())
            {
                IsBackground = true,
                Name = "iOSFakeRun exit reset"
            };

            resetThread.Start();

            if (!resetThread.Join(TimeSpan.FromSeconds(10)))
            {
                MessageBox.Show("定位复位未能在 10 秒内完成\n手机可能仍停留在模拟位置（重启手机也会恢复）");
            }
        }

        DisconnectDevice();

        if (CheckBoxStopAppleOnExit.IsChecked == true)
        {
            AppleDeviceService.Stop(out _);
        }

        base.OnClosing(e);
    }
}
