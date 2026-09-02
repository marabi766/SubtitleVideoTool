using System.Windows;
using Microsoft.Win32;

namespace SubtitleVideoTool.App;

public partial class App : Application
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private UserPreferenceChangedEventHandler? themeWatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplyTheme(IsSystemDark());

        // Windows raises this when the user flips light/dark while we're running.
        // SystemEvents is a static, process-wide event, so the handler is kept
        // in a field to be detached on exit rather than left rooted.
        themeWatcher = (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General)
            {
                Dispatcher.Invoke(() => ApplyTheme(IsSystemDark()));
            }
        };

        SystemEvents.UserPreferenceChanged += themeWatcher;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (themeWatcher is not null)
        {
            SystemEvents.UserPreferenceChanged -= themeWatcher;
            themeWatcher = null;
        }

        base.OnExit(e);
    }

    private ResourceDictionary? palette;

    private void ApplyTheme(bool dark)
    {
        // A pack URI, not a relative one: this runs after startup, when relative
        // resolution is against the running directory rather than the assembly.
        var source = new Uri(
            dark
                ? "pack://application:,,,/Theme/Dark.xaml"
                : "pack://application:,,,/Theme/Light.xaml",
            UriKind.Absolute);

        var replacement = new ResourceDictionary { Source = source };

        // The palette is tracked by reference and always sits last, so it wins
        // over WPF's own Fluent dictionary no matter where that gets inserted.
        if (palette is not null && Resources.MergedDictionaries.Remove(palette))
        {
            // Removed in place; the new one is appended below.
        }

        Resources.MergedDictionaries.Add(replacement);
        palette = replacement;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // The value is "apps use LIGHT theme", so 0 means dark.
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            // A machine that won't answer gets the light theme, which is the
            // Windows default anyway.
            return false;
        }
    }
}
