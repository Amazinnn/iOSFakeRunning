using iMobileDevice;
using iMobileDevice.Lockdown;

namespace iOSFakeRun.FakeRun;

internal static class DeviceUtils
{
    public static bool GetName(LockdownClientHandle? lockdownClient, out string deviceName)
    {
        deviceName = "";

        return LibiMobileDevice.Instance.Lockdown.lockdownd_get_device_name(lockdownClient, out deviceName) == LockdownError.Success;
    }

    public static bool GetVersion(LockdownClientHandle? lockdownClient, out string iosVersion)
    {
        iosVersion = "";

        if (LibiMobileDevice.Instance.Lockdown.lockdownd_get_value(lockdownClient, null, "ProductVersion", out var plistHandle) != LockdownError.Success)
        {
            return false;
        }

        using (plistHandle)
        {
            LibiMobileDevice.Instance.Plist.plist_get_string_val(plistHandle, out iosVersion);

            if (string.IsNullOrEmpty(iosVersion))
            {
                return false;
            }

            // The developer image folder is keyed by major.minor (e.g. "16.7"), so patch versions collapse.
            var parts = iosVersion.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            iosVersion = parts[0] + "." + parts[1];

            return true;
        }
    }
}