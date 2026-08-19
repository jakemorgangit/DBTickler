using System.Windows;
using System.Windows.Input;

namespace DBTickler.App.Views;

/// <summary>
/// A prompt for a single value. Replaces v1's use of <c>Microsoft.VisualBasic.Interaction.InputBox</c>,
/// which pulled the whole Visual Basic runtime into the published binary for one dialog and
/// could not be themed.
/// </summary>
public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        ResponseInput.Text = initialValue;

        Loaded += (_, _) =>
        {
            ResponseInput.SelectAll();
            ResponseInput.Focus();
        };
    }

    public string ResponseText => ResponseInput.Text;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        DialogResult = true;
        Close();
    }
}
