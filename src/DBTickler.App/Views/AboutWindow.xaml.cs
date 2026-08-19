using System.Reflection;
using System.Windows;

namespace DBTickler.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "2.0.0";

        // Source-link builds append a commit hash after a '+'; it is noise in a dialog.
        var plusIndex = version.IndexOf('+');
        VersionText.Text = $"Version {(plusIndex > 0 ? version[..plusIndex] : version)}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
