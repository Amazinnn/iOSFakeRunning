using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;
using iMobileDevice.MobileImageMounter;

namespace iOSFakeRun.FakeRun;

internal static class Image
{
    private const string RootFolderName = "DeveloperDiskImage";

    private const string ImageFileName = "DeveloperDiskImage.dmg";

    private const string SignatureFileName = "DeveloperDiskImage.dmg.signature";

    public static bool MountImage(iDeviceHandle idevice, LockdownClientHandle? lockdownClient, string iosVersion, out string error)
    {
        error = string.Empty;

        var mounterInstance = LibiMobileDevice.Instance.MobileImageMounter;
        var lockdownInstance = LibiMobileDevice.Instance.Lockdown;
        var plistInstance = LibiMobileDevice.Instance.Plist;

        if (lockdownInstance.lockdownd_start_service(lockdownClient, "com.apple.mobile.mobile_image_mounter", out var lockdownServiceDescriptor) !=
            LockdownError.Success)
        {
            error = "无法启动镜像挂载服务";
            return false;
        }

        using (lockdownServiceDescriptor)
        {
            if (mounterInstance.mobile_image_mounter_new(idevice, lockdownServiceDescriptor, out var mounterClient) != MobileImageMounterError.Success)
            {
                error = "无法建立镜像挂载连接";
                return false;
            }

            using (mounterClient)
            {
                try
                {
                    if (mounterInstance.mobile_image_mounter_lookup_image(mounterClient, "Developer", out var plist) != MobileImageMounterError.Success)
                    {
                        error = "查询设备镜像状态失败";
                        return false;
                    }

                    using (plist)
                    {
                        var array = plistInstance.plist_dict_get_item(plist, "ImageSignature");
                        using (array)
                        {
                            var size = plistInstance.plist_array_get_size(array);
                            if (size > 0)
                            {
                                // Already mounted.
                                return true;
                            }
                        }
                    }

                    var directory = ResolveImageDirectory(iosVersion);

                    if (directory == null)
                    {
                        var available = AvailableVersions();

                        error = $"缺少 iOS {iosVersion} 的开发者镜像\n" +
                                $"请在 {RootFolderName} 文件夹下新建 {iosVersion} 文件夹，放入 {ImageFileName} 与其 .signature 文件";

                        if (available.Count > 0)
                        {
                            error += "\n当前可用版本: " + string.Join(", ", available);
                        }

                        return false;
                    }

                    var image = File.ReadAllBytes(Path.Combine(directory, ImageFileName));
                    var imageSign = File.ReadAllBytes(Path.Combine(directory, SignatureFileName));

                    var offset = 0;

                    if (mounterInstance.mobile_image_mounter_upload_image(mounterClient, "Developer", (uint) image.Length, imageSign, (ushort) imageSign.Length,
                            (buffer, length, _) =>
                            {
                                Marshal.Copy(image, offset, buffer, (int) length);
                                offset += (int) length;

                                return (int) length;
                            }, new IntPtr(0))
                        != MobileImageMounterError.Success)
                    {
                        error = "上传开发者镜像失败\n请确保设备未处于锁屏状态";
                        return false;
                    }

                    if (mounterInstance.mobile_image_mounter_mount_image(mounterClient, "/private/var/mobile/Media/PublicStaging/staging.dimage", imageSign,
                            (ushort) imageSign.Length, "Developer", out plist) != MobileImageMounterError.Success)
                    {
                        error = "挂载开发者镜像失败\n请确保设备未处于锁屏状态";
                        return false;
                    }

                    using (plist)
                    {
                        uint length = 0;
                        plistInstance.plist_to_xml(plist, out var plistXml, ref length);

                        if (!plistXml.Contains("Complete") || plistXml.Contains("ImageMountFailed"))
                        {
                            error = "挂载开发者镜像失败\n镜像被设备拒绝，请确认镜像版本与系统版本匹配";
                            return false;
                        }
                    }
                }
                catch (Exception exception)
                {
                    error = "挂载开发者镜像失败\n" + exception.Message;
                    return false;
                }
                finally
                {
                    if (mounterClient != null && !mounterClient.IsClosed)
                    {
                        mounterInstance.mobile_image_mounter_hangup(mounterClient);
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the folder holding the image for a given iOS version. Searches the working directory and
    /// then walks up from the executable, so a debug build under bin/ can still find the repository copy.
    /// </summary>
    public static string? ResolveImageDirectory(string iosVersion)
    {
        foreach (var root in AppPaths.CandidateRoots())
        {
            var directory = Path.Combine(root, RootFolderName, iosVersion);
            var image = Path.Combine(directory, ImageFileName);
            var signature = Path.Combine(directory, SignatureFileName);

            if (File.Exists(image) && File.Exists(signature))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>iOS versions for which a usable image pair was found.</summary>
    public static List<string> AvailableVersions()
    {
        var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in AppPaths.CandidateRoots())
        {
            var folder = Path.Combine(root, RootFolderName);

            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var directory in Directory.GetDirectories(folder))
                {
                    var image = Path.Combine(directory, ImageFileName);
                    var signature = Path.Combine(directory, SignatureFileName);

                    if (File.Exists(image) && File.Exists(signature))
                    {
                        versions.Add(Path.GetFileName(directory));
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable candidate folder simply contributes no versions.
            }
        }

        return versions.ToList();
    }
}
