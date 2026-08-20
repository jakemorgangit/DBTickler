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
            Record("Unhandled exception", args.ExceptionObject as Exception, Severity.Fatal);

        DispatcherUnhandledException += (_, args) =>
        {
            Record("UI thread exception", args.Exception, Severity.Recoverable);
            args.Handled = true;
        };

        // Written to the log but never surfaced as a dialog. An unobserved task exception is
        // reported by the finaliser long after the work was abandoned, so there is nothing the
        // operator can do about it and no way to tell how many are the same fault repeating —
        // interrupting a running workload with a modal box per occurrence is worse than the
        // fault itself.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record("Background task exception", args.Exception, Severity.Silent);
            args.SetObserved();
        };
    }

    private enum Severity
    {
        /// <summary>Logged only; the operator is not interrupted.</summary>
        Silent,

        /// <summary>Logged and reported, but the application keeps running.</summary>
        Recoverable,

        /// <summary>Logged and reported; the process is going down.</summary>
        Fatal,
    }

    private static void Record(string context, Exception? exception, Severity severity)
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

        if (severity == Severity.Silent) return;

        MessageBox.Show(
            $"{exception?.Message ?? context}\n\nDetails were written to:\n{CrashLogPath}",
            severity == Severity.Fatal ? "DBTickler — fatal error" : "DBTickler — error",
            MessageBoxButton.OK,
            severity == Severity.Fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
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
