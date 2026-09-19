using System;
using System.Globalization;
using System.Text;
using System.Threading;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;
using iMobileDevice.Service;

namespace iOSFakeRun.FakeRun;

/// <summary>
/// Writes positions to the device's location simulation service.
///
/// Connection semantics, measured on iOS 12.5: the device takes one location per connection and
/// closes it, so every write opens its own connection, sends one message and closes it. Reusing a
/// connection for a second message wedges the device library's send — it spins forever, without
/// succeeding or erroring. The same wedge happens on any other dead connection, which is why every
/// native call below runs on a deadline thread: past the deadline the attempt is declared hung and
/// the thread is abandoned (it can never be killed from outside).
///
/// The piece upstream lacks and this class adds is self-healing. The userspace Apple stack on
/// Windows (AppleMobileDeviceProcess) and the phone's own lockdown session both die quietly —
/// while the app sits connected, between runs, overnight. Every handle built before such a death
/// then points into a dead connection, and the first write wedges. On failure this session
/// therefore rebuilds the device handles through a caller-supplied factory and retries once; both
/// the write and the rebuild run on deadline threads, so no path through this class can hang
/// forever. Dead handles are leaked, never disposed, because the abandoned thread may still be
/// executing native code inside them and disposal would be a use-after-free.
/// </summary>
internal sealed class LocationSession : ILocationSink, IDisposable
{
    /// <summary>Builds a fresh device handle pair, bringing the Apple stack up if needed.</summary>
    public delegate bool DeviceHandleFactory(out iDeviceHandle? idevice, out LockdownClientHandle? lockdown, out string error);

    private const string ServiceName = "com.apple.dt.simulatelocation";

    private const uint OpcodeSet = 0u;
    private const uint OpcodeReset = 1u;

    /// <summary>How long one attempt may take before it is declared hung.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Budget for rebuilding the handles. Covers starting the whole Apple stack (up to 30 s) plus a
    /// bounded lockdown handshake (15 s), both of which are needed when the userspace mux died
    /// while idle. A factory that needs longer is abandoned and counted, like a wedged write.
    /// </summary>
    private static readonly TimeSpan RebuildTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Upper bound on abandoned threads before further writes fail fast. Each entry is one leaked
    /// connection pair; upstream lives in this state permanently (it leaks a pair per coordinate),
    /// so a handful is harmless — the cap only stops a pathological run from piling up threads.
    /// </summary>
    private const int MaxAbandoned = 8;

    private readonly DeviceHandleFactory _factory;
    private readonly object _gate = new();

    private iDeviceHandle? _idevice;
    private LockdownClientHandle? _lockdown;
    private volatile bool _disposed;
    private int _abandoned;

    public string LastFailure { get; private set; } = "";

    private LocationSession(DeviceHandleFactory factory, iDeviceHandle? idevice, LockdownClientHandle? lockdown)
    {
        _factory = factory;
        _idevice = idevice;
        _lockdown = lockdown;
    }

    /// <summary>
    /// Builds a session through the factory and checks that the location service can be reached at
    /// all, so a missing or mismatched developer image is reported at connect time rather than when
    /// a run starts.
    /// </summary>
    public static LocationSession? Open(DeviceHandleFactory factory, out string error)
    {
        error = string.Empty;

        if (!factory(out var idevice, out var lockdown, out error))
        {
            return null;
        }

        var session = new LocationSession(factory, idevice, lockdown);

        var probeStage = session.Probe(idevice!, lockdown!);

        if (probeStage.Length > 0)
        {
            session.Settle(idevice, lockdown, hung: false);
            error = "无法启动定位服务（" + probeStage + "）\n请确认开发者镜像已挂载、设备已解锁并重新点击连接";
            return null;
        }

        return session;
    }

    /// <summary>
    /// Opens and immediately closes one connection to check the service is reachable. Runs on a
    /// deadline thread: at connect time the device can be just as wedged as mid-run, and the probe
    /// must fail with a message instead of freezing the UI thread forever.
    /// </summary>
    /// <returns>An empty string when reachable, otherwise the failure stage.</returns>
    private string Probe(iDeviceHandle idevice, LockdownClientHandle lockdown)
    {
        var stage = "";
        using var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                var service = OpenService(idevice, lockdown, out var openStage);

                if (service == null)
                {
                    stage = openStage;
                    return;
                }

                service.Dispose();
            }
            catch (Exception)
            {
                stage = "调用异常";
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "iOSFakeRun location probe"
        };

        thread.Start();

        if (!finished.Wait(WriteTimeout))
        {
            stage = "定位服务无响应（连接已失效）";
        }

        return stage;
    }

    public bool Set(double latitude, double longitude)
    {
        return Write(service => SendUInt(service, OpcodeSet) &&
                                SendString(service, latitude.ToString(CultureInfo.InvariantCulture)) &&
                                SendString(service, longitude.ToString(CultureInfo.InvariantCulture)));
    }

    public bool Reset()
    {
        return Write(service => SendUInt(service, OpcodeReset));
    }

    /// <summary>
    /// One write: try the current handles, and on any failure rebuild them through the factory and
    /// try once more. The retry is what turns "the stack died while idle" from a dead run into a
    /// few seconds of silence.
    /// </summary>
    private bool Write(Func<ServiceClientHandle, bool> body)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                LastFailure = "会话已关闭";
                return false;
            }

            var pairDevice = _idevice!;
            var pairLockdown = _lockdown!;
            var first = Attempt(body, pairDevice, pairLockdown);

            if (first.Success)
            {
                LastFailure = string.Empty;
                return true;
            }

            if (_disposed)
            {
                LastFailure = "会话已关闭";
                return false;
            }

            Settle(pairDevice, pairLockdown, first.Hung);

            if (_abandoned >= MaxAbandoned)
            {
                LastFailure = "定位服务多次无响应\n请重启本程序后重试";
                return false;
            }

            if (!RebuildHandles(out var rebuildError))
            {
                LastFailure = rebuildError;
                return false;
            }

            var retryDevice = _idevice!;
            var retryLockdown = _lockdown!;
            var second = Attempt(body, retryDevice, retryLockdown);

            if (second.Success)
            {
                LastFailure = string.Empty;
                return true;
            }

            Settle(retryDevice, retryLockdown, second.Hung);
            LastFailure = second.Failure ?? "定位服务无响应";
            return false;
        }
    }

    private AttemptResult Attempt(Func<ServiceClientHandle, bool> body, iDeviceHandle idevice, LockdownClientHandle lockdown)
    {
        var result = new AttemptResult();
        using var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                var service = OpenService(idevice, lockdown, out var stage);

                if (service == null)
                {
                    result.Failure = "定位服务无法启动（" + stage + "）";
                    return;
                }

                using (service)
                {
                    if (!body(service))
                    {
                        result.Failure = "定位服务拒绝写入";
                    }
                }
            }
            catch (Exception)
            {
                result.Failure = "定位服务调用异常";
            }
            finally
            {
                finished.Set();
            }
        })
        {
            // Background, so a writer that never comes back cannot keep the process alive.
            IsBackground = true,
            Name = "iOSFakeRun location write"
        };

        thread.Start();

        if (!finished.Wait(WriteTimeout))
        {
            // The thread is wedged inside a native call and owns the connection. The handles stay
            // alone with it; SettleHandlesAfterFailure replaces ours and accounts for the leak.
            result.Hung = true;
            result.Failure = "定位服务无响应（连接已失效）";
        }

        return result;
    }

    /// <summary>
    /// After a failed attempt on a handle pair: a hung attempt means a wedged thread may still be
    /// executing native code inside that pair, so it is leaked (and counted); a clean failure owns
    /// nothing and the pair is disposed. Either way the session's pair reference is dropped.
    /// </summary>
    private void Settle(iDeviceHandle? idevice, LockdownClientHandle? lockdown, bool hung)
    {
        if (hung)
        {
            _abandoned++;
        }
        else
        {
            idevice?.Dispose();
            lockdown?.Dispose();
        }

        if (ReferenceEquals(_idevice, idevice))
        {
            _idevice = null;
        }

        if (ReferenceEquals(_lockdown, lockdown))
        {
            _lockdown = null;
        }
    }

    /// <summary>
    /// Rebuilds the device handle pair through the factory. The factory involves native calls —
    /// possibly a full lockdown handshake — that can wedge exactly like a write can, so it runs on
    /// its own deadline thread instead of inside the lock this is called from.
    /// </summary>
    private bool RebuildHandles(out string error)
    {
        _idevice = null;
        _lockdown = null;

        iDeviceHandle? idevice = null;
        LockdownClientHandle? lockdown = null;
        var ok = false;
        var rebuildError = string.Empty;

        using var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                ok = _factory(out idevice, out lockdown, out rebuildError);
            }
            catch (Exception)
            {
                ok = false;
                rebuildError = "重建设备连接时异常";
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "iOSFakeRun handle rebuild"
        };

        thread.Start();

        if (!finished.Wait(RebuildTimeout))
        {
            // The factory is wedged inside native code; whatever handles it made stay with it and
            // count against the abandoned budget, like a wedged write does.
            _abandoned++;
            error = "重建设备连接超时（Apple 设备服务或手机无响应）";
            return false;
        }

        error = rebuildError;

        if (ok)
        {
            _idevice = idevice;
            _lockdown = lockdown;
        }

        return ok;
    }

    /// <summary>Opens one connection to the location service; the caller owns and closes it.</summary>
    private ServiceClientHandle? OpenService(iDeviceHandle idevice, LockdownClientHandle lockdown, out string stage)
    {
        stage = "启动定位服务";

        if (_lockdown == null ||
            LibiMobileDevice.Instance.Lockdown.lockdownd_start_service(lockdown, ServiceName, out var descriptor) != LockdownError.Success)
        {
            stage = "会话已失效或镜像未挂载";
            return null;
        }

        using (descriptor)
        {
            stage = "建立连接";

            if (LibiMobileDevice.Instance.Service.service_client_new(idevice, descriptor, out var client) != ServiceError.Success)
            {
                return null;
            }

            return client;
        }
    }

    private static bool SendUInt(ServiceClientHandle service, uint value)
    {
        var bytes = BitConverter.GetBytes(value);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return SendAll(service, bytes);
    }

    private static bool SendString(ServiceClientHandle service, string value)
    {
        var payload = Encoding.UTF8.GetBytes(value);
        var length = BitConverter.GetBytes((uint)payload.Length);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(length);
        }

        return SendAll(service, length) && SendAll(service, payload);
    }

    private static bool SendAll(ServiceClientHandle service, byte[] buffer)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var chunk = new byte[buffer.Length - offset];
            Buffer.BlockCopy(buffer, offset, chunk, 0, chunk.Length);

            var sent = 0u;

            if (LibiMobileDevice.Instance.Service.service_send(service, chunk, (uint)chunk.Length, ref sent) != ServiceError.Success || sent == 0)
            {
                return false;
            }

            offset += (int)sent;
        }

        return true;
    }

    /// <summary>
    /// Stops the session. Deliberately takes no lock and disposes nothing: an in-flight write may
    /// be inside native code on the current handles right now, and waiting out a wedged write would
    /// freeze the UI for up to a minute. The flag turns the next write into a no-op; the current
    /// pair is dropped unreferenced, which leaks at most one pair per disconnect.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _idevice = null;
        _lockdown = null;
    }

    private sealed class AttemptResult
    {
        public bool Success => Failure == null;

        public bool Hung { get; set; }

        public string? Failure { get; set; }
    }
}
