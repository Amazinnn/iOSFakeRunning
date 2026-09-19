using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Win32;

namespace iOSFakeRun.FakeRun;

/// <summary>
/// Manages the Apple device communication stack (the usbmux endpoint that libimobiledevice talks to).
///
/// On a machine with the Microsoft Store "Apple Devices" app there is no Windows service at all: a
/// per-user <c>AppleMobileDeviceLauncher.exe</c> supervises <c>AppleMobileDeviceProcess.exe</c>, which
/// listens on 127.0.0.1:27015. Both are ordinary same-user processes, so stopping them needs no elevation.
///
/// Starting them is the awkward part. The packaged launcher cannot be launched by path — that fails with
/// "access denied" because packaged apps are protected by the AppModel — and neither the execution alias
/// nor activating the agent's own AUMID brings the stack up. The only activation that works is the
/// package's main application AUMID, which also shows the Apple Devices window; that window is closed
/// again once the mux endpoint is listening, and the launcher survives its closure.
/// </summary>
internal static class AppleDeviceService
{
    public const string LauncherProcessName = "AppleMobileDeviceLauncher";

    public const string MuxProcessName = "AppleMobileDeviceProcess";

    private const string UserInterfaceProcessName = "AppleDevices";

    private const string PackageFolderPrefix = "AppleInc.AppleDevices_";

    /// <summary>
    /// Per-user package repository. Readable without elevation, unlike the WindowsApps directory itself,
    /// which cannot even be listed by a standard user.
    /// </summary>
    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>The application entry point inside the package, as declared in its manifest.</summary>
    private const string ApplicationId = "App";

    private const int MuxPort = 27015;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to keep watching for the Apple Devices window so it can be closed again.</summary>
    private static readonly TimeSpan InterfaceCloseTimeout = TimeSpan.FromSeconds(20);

    /// <summary>True when either half of the stack is alive.</summary>
    public static bool IsRunning()
    {
        return IsProcessRunning(LauncherProcessName) || IsProcessRunning(MuxProcessName);
    }

    /// <summary>True when something is actually accepting connections on the usbmux port.</summary>
    public static bool IsMuxListening()
    {
        try
        {
            using var client = new TcpClient();

            return client.ConnectAsync("127.0.0.1", MuxPort).Wait(TimeSpan.FromMilliseconds(800)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string Describe()
    {
        if (IsMuxListening())
        {
            return "已运行";
        }

        return IsRunning() ? "启动中" : "未运行";
    }

    /// <summary>
    /// Brings the stack up if it is not already listening, and waits for the usbmux port to accept
    /// connections. Any Apple Devices window opened as a side effect is closed again.
    /// </summary>
    public static bool EnsureRunning(out string message)
    {
        message = string.Empty;

        if (IsMuxListening())
        {
            message = "Apple 设备服务已在运行";
            return true;
        }

        var aumid = FindApplicationUserModelId();

        if (aumid == null)
        {
            message = "未找到 Apple 设备组件\n请先安装 Apple Devices（Microsoft Store）或 iTunes";
            return false;
        }

        var interfaceWasRunning = IsProcessRunning(UserInterfaceProcessName);

        try
        {
            // Launches through the shell so the package gets its required identity.
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            message = "启动 Apple 设备服务失败\n" + exception.Message;
            return false;
        }

        var deadline = DateTime.UtcNow + StartupTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (IsMuxListening())
            {
                var closed = CloseUserInterface(interfaceWasRunning);

                message = closed
                    ? "Apple 设备服务已启动（已自动关闭弹出的 Apple Devices 窗口）"
                    : "Apple 设备服务已启动";

                return true;
            }

            Thread.Sleep(500);
        }

        message = "Apple 设备服务启动超时\n请手动打开一次 Apple Devices 后再试";
        return false;
    }

    /// <summary>
    /// Stops the stack. The launcher is killed first: it supervises the mux process and would
    /// otherwise bring it straight back.
    /// </summary>
    public static bool Stop(out string message)
    {
        message = string.Empty;

        if (!IsRunning() && !IsMuxListening())
        {
            message = "Apple 设备服务未在运行";
            return true;
        }

        var failed = new List<string>();

        KillAll(LauncherProcessName, failed);
        KillAll(MuxProcessName, failed);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline && IsMuxListening())
        {
            Thread.Sleep(200);
        }

        if (IsMuxListening())
        {
            message = "Apple 设备服务未能完全停止" + (failed.Count > 0 ? "\n" + string.Join("\n", failed) : string.Empty);
            return false;
        }

        message = "Apple 设备服务已停止";
        return true;
    }

    /// <summary>Closes the Apple Devices window we opened; the device stack is unaffected by it.</summary>
    private static bool CloseUserInterface(bool interfaceWasRunning)
    {
        if (interfaceWasRunning)
        {
            return false;
        }

        // The window takes longer to appear than the mux endpoint does, so keep watching for it
        // rather than checking only once right after the port comes up.
        var deadline = DateTime.UtcNow + InterfaceCloseTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (TryCloseUserInterface())
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return false;
    }

    /// <summary>True once a window was successfully closed.</summary>
    private static bool TryCloseUserInterface()
    {
        Process[] processes;

        try
        {
            processes = Process.GetProcessesByName(UserInterfaceProcessName);
        }
        catch (Exception)
        {
            return false;
        }

        var closed = false;

        foreach (var process in processes)
        {
            try
            {
                process.Refresh();

                // Still starting up: there is no window to close yet.
                if (process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                if (process.CloseMainWindow())
                {
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }

                    closed = true;
                }
            }
            catch (Exception)
            {
                // If the window cannot be closed it is only a cosmetic annoyance.
            }
            finally
            {
                process.Dispose();
            }
        }

        return closed;
    }

    private static bool KillAll(string processName, List<string> failed)
    {
        Process[] processes;

        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception exception)
        {
            failed.Add(processName + ": " + exception.Message);
            return false;
        }

        var allKilled = true;

        foreach (var process in processes)
        {
            try
            {
                process.Kill();
                process.WaitForExit(3000);
            }
            catch (Exception exception)
            {
                failed.Add(processName + ": " + exception.Message);
                allKilled = false;
            }
            finally
            {
                process.Dispose();
            }
        }

        return allKilled;
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            var running = processes.Length > 0;

            foreach (var process in processes)
            {
                process.Dispose();
            }

            return running;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The installed Apple Devices package full name, e.g.
    /// "AppleInc.AppleDevices_1.1540.24088.0_x64__nzyj5cx40ttqa", or null when it is not installed.
    /// </summary>
    public static string? FindPackageFullName()
    {
        try
        {
            using var repository = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey);

            if (repository != null)
            {
                var name = repository.GetSubKeyNames()
                    .Where(candidate => candidate.StartsWith(PackageFolderPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(ParseVersionFromFullName)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the directory probe below.
        }

        return FindPackageDirectory() is { } directory ? Path.GetFileName(directory) : null;
    }

    /// <summary>
    /// The newest Apple Devices package folder, or null when it cannot be determined.
    /// Tries the per-user package repository first (works unelevated), then falls back to listing
    /// WindowsApps, which only succeeds from an elevated process.
    /// </summary>
    public static string? FindPackageDirectory()
    {
        var fullName = FindPackageFullNameFromRepository();

        if (fullName != null)
        {
            try
            {
                using var package = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey + "\\" + fullName);

                if (package?.GetValue("PackageRootFolder") is string root && Directory.Exists(root))
                {
                    return root;
                }
            }
            catch (Exception)
            {
                // Fall through to the directory probe below.
            }
        }

        return FindPackageDirectoryByListing();
    }

    private static string? FindPackageFullNameFromRepository()
    {
        try
        {
            using var repository = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey);

            if (repository == null)
            {
                return null;
            }

            return repository.GetSubKeyNames()
                .Where(candidate => candidate.StartsWith(PackageFolderPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ParseVersionFromFullName)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindPackageDirectoryByListing()
    {
        var roots = new List<string>();

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            roots.Add(Path.Combine(programFiles, "WindowsApps"));
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            roots.Add(Path.Combine(programFilesX86, "WindowsApps"));
        }

        var candidates = new List<(Version Version, string Path)>();

        foreach (var root in roots.Distinct())
        {
            string[] directories;

            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                directories = Directory.GetDirectories(root, PackageFolderPrefix + "*");
            }
            catch (Exception)
            {
                // A standard user cannot list WindowsApps; the registry path covers that case.
                continue;
            }

            foreach (var directory in directories)
            {
                candidates.Add((ParsePackageVersion(Path.GetFileName(directory)), directory));
            }
        }

        return candidates.Count == 0
            ? null
            : candidates.OrderByDescending(candidate => candidate.Version).First().Path;
    }

    /// <summary>
    /// Builds the AUMID used to activate the package, e.g. "AppleInc.AppleDevices_nzyj5cx40ttqa!App".
    /// The publisher hash differs per machine, so it is read from the package full name rather than
    /// hard coded: "AppleInc.AppleDevices_&lt;version&gt;_&lt;arch&gt;__&lt;publisherId&gt;".
    /// </summary>
    public static string? FindApplicationUserModelId()
    {
        var fullName = FindPackageFullName();

        if (fullName == null)
        {
            return null;
        }

        var parts = fullName.Split(new[] {"__"}, StringSplitOptions.None);

        if (parts.Length < 2)
        {
            return null;
        }

        var publisherId = parts[^1];
        var nameParts = parts[0].Split('_');
        var packageName = nameParts.Length > 0 ? nameParts[0] : string.Empty;

        if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(publisherId))
        {
            return null;
        }

        return $"{packageName}_{publisherId}!{ApplicationId}";
    }

    public static string? FindLauncherPath()
    {
        var directory = FindPackageDirectory();

        if (directory == null)
        {
            return null;
        }

        var path = Path.Combine(directory, "AppleMobileDeviceLauncher.exe");

        return File.Exists(path) ? path : null;
    }

    private static Version ParseVersionFromFullName(string fullName)
    {
        var parts = fullName.Split('_');

        return parts.Length >= 2 && Version.TryParse(parts[1], out var version) ? version : new Version(0, 0);
    }

    private static Version ParsePackageVersion(string directoryName)
    {
        return ParseVersionFromFullName(directoryName);
    }
}
