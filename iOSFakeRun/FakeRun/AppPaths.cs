using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace iOSFakeRun.FakeRun;

/// <summary>
/// Resolves runtime files that live next to the sources rather than next to the executable.
/// A debug build under bin/ or a published build under dist/ both need to find the repository's
/// DeveloperDiskImage folder and the saved route, so candidates are walked upwards from the executable.
/// </summary>
internal static class AppPaths
{
    public const string RouteSaveFileName = "route.save";

    public const string ErrorLogFileName = "error.log";

    /// <summary>Working directory first, then each ancestor of the executable's directory.</summary>
    public static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? working = null;

        try
        {
            working = Directory.GetCurrentDirectory();
        }
        catch (Exception)
        {
            // A missing working directory just means there is no candidate from it.
        }

        if (!string.IsNullOrEmpty(working) && seen.Add(working))
        {
            yield return working;
        }

        var directory = AppContext.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar);

        for (var level = 0; level < 6 && !string.IsNullOrEmpty(directory); level++)
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }

            directory = Path.GetDirectoryName(directory)?.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    /// <summary>The first existing candidate for a file, or null when none of them exist.</summary>
    public static string? ResolveExistingFile(string fileName)
    {
        foreach (var root in CandidateRoots())
        {
            try
            {
                var candidate = Path.Combine(root, fileName);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // An unusable candidate root is simply skipped.
            }
        }

        return null;
    }

    /// <summary>
    /// Where a file should be written: alongside an existing copy so the user keeps one file, otherwise
    /// in the working directory.
    /// </summary>
    public static string ResolveWritePath(string fileName)
    {
        var existing = ResolveExistingFile(fileName);

        if (existing != null)
        {
            return existing;
        }

        try
        {
            return Path.Combine(Directory.GetCurrentDirectory(), fileName);
        }
        catch (Exception)
        {
            return Path.Combine(AppContext.BaseDirectory ?? ".", fileName);
        }
    }
}
