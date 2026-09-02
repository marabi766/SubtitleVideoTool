using System.Diagnostics;

namespace SubtitleVideoTool.Core;

/// <summary>
/// The "Sign in to YouTube" path. Firefox is opened on a profile folder that
/// belongs to this program alone, so the user signs in once without the program
/// ever reading, copying or displaying the cookies of their everyday browser.
/// </summary>
/// <remarks>
/// Firefox is the only browser whose cookie store yt-dlp can read from an
/// arbitrary profile folder on Windows. Chromium profiles are sealed with DPAPI
/// plus app-bound encryption, which is exactly the failure the design's
/// "Your browser's cookies are locked" card describes.
/// </remarks>
public static class SignIn
{
    public static IEnumerable<string> FirefoxCandidatePaths()
    {
        string?[] roots =
        [
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        ];

        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, "Mozilla Firefox", "firefox.exe");
            }
        }
    }

    public static string? FindFirefox(IEnumerable<string>? searchPaths = null) =>
        (searchPaths ?? FirefoxCandidatePaths()).FirstOrDefault(File.Exists);

    public static string ProfileFolder(string? root = null)
    {
        root ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "SubtitleVideoTool", "youtube-signin-profile");
    }

    public static IReadOnlyList<string> BrowserArguments(string profileFolder, string url = "https://www.youtube.com/") =>
        ["-no-remote", "-profile", profileFolder, url];

    /// <summary>Whether a sign-in has actually happened in that profile yet.</summary>
    public static bool IsProfileReady(string profileFolder)
    {
        if (string.IsNullOrWhiteSpace(profileFolder) || !Directory.Exists(profileFolder))
        {
            return false;
        }

        var database = Path.Combine(profileFolder, "cookies.sqlite");
        return File.Exists(database) && new FileInfo(database).Length > 0;
    }

    /// <summary>Opens the sign-in window. Returns false when Firefox is absent.</summary>
    public static bool Open(string profileFolder)
    {
        var firefox = FindFirefox();
        if (firefox is null)
        {
            return false;
        }

        Directory.CreateDirectory(profileFolder);

        var startInfo = new ProcessStartInfo { FileName = firefox, UseShellExecute = false };
        foreach (var argument in BrowserArguments(profileFolder))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
        return true;
    }
}
