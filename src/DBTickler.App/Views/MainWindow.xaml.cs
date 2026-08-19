using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DBTickler.App.Services;
using DBTickler.App.ViewModels;
using Microsoft.Win32;

namespace DBTickler.App.Views;

public partial class MainWindow : Window, IUserInteraction
{
    /// <summary>DWM attribute that paints the title bar to match a dark application theme.</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(this);
        DataContext = _viewModel;

        _viewModel.PasswordRestored += password => PasswordInput.Password = password;
        _viewModel.ThemeChanged += ApplyTheme;
        _viewModel.Log.LinesAppended += ScrollLogToEnd;

        Loaded += (_, _) => ApplyTheme(_viewModel.IsDarkTheme);
        Closed += (_, _) =>
        {
            _viewModel.Log.LinesAppended -= ScrollLogToEnd;
            _viewModel.Dispose();
        };
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Password = PasswordInput.Password;

    private void OnToggleTheme(object sender, RoutedEventArgs e) =>
        _viewModel.IsDarkTheme = !_viewModel.IsDarkTheme;

    private void OnShowAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void ApplyTheme(bool isDark)
    {
        if (Application.Current is App app)
            app.ApplyTheme(isDark);

        ApplyTitleBarTheme(isDark);
    }

    private void ApplyTitleBarTheme(bool isDark)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var value = isDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Windows versions before the attribute existed simply keep the light title bar.
        }
    }

    // DllImport rather than LibraryImport: the source-generated variant requires the whole
    // project to allow unsafe code, which is a lot to enable for one call.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    // ── IUserInteraction ──

    public bool Confirm(string title, string message, string confirmButtonText = "Continue") =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            == MessageBoxResult.OK;

    public string? PromptForText(string title, string prompt, string initialValue = "")
    {
        var dialog = new TextInputDialog(title, prompt, initialValue) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.ResponseText : null;
    }

    public string? PromptForSavePath(string title, string filter, string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public string? PromptForOpenPath(string title, string filter, string initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            InitialDirectory = initialDirectory,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
