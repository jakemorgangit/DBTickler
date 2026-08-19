using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DBTickler.App;

public partial class App : Application
{
    /// <summary>
    /// Crash logs go next to the user's other application data. v1 wrote them to the current
    /// working directory, which for a portable executable launched from Program Files or a
    /// read-only share means the log fails to write at the exact moment it is needed.
    /// </summary>
    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DBTickler",
        "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record("Unhandled exception", args.ExceptionObject as Exception, fatal: true);

        DispatcherUnhandledException += (_, args) =>
        {
            Record("UI thread exception", args.Exception, fatal: false);
            args.Handled = true;
        };

        // A faulted background task must not take the process down; the load engine already
        // handles its own failures, so anything arriving here is worth logging but survivable.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record("Background task exception", args.Exception, fatal: false);
            args.SetObserved();
        };
    }

    private static void Record(string context, Exception? exception, bool fatal)
    {
        var message = exception?.ToString() ?? "(no exception details)";

        try
        {
            var path = CrashLogPath;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} — {context}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing useful to do if even the crash log cannot be written.
        }

        MessageBox.Show(
            $"{exception?.Message ?? context}\n\nDetails were written to:\n{CrashLogPath}",
            fatal ? "DBTickler — fatal error" : "DBTickler — error",
            MessageBoxButton.OK,
            fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
    }

    /// <summary>Swaps the palette dictionary, leaving the control styles in place.</summary>
    public void ApplyTheme(bool isDark)
    {
        var palette = new ResourceDictionary
        {
            Source = new Uri(
                isDark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml",
                UriKind.Absolute),
        };

        Resources.MergedDictionaries[0] = palette;
    }
}
