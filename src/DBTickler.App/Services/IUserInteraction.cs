namespace DBTickler.App.Services;

/// <summary>
/// Dialogs, abstracted away from the view models. Keeps message boxes and file pickers out
/// of view-model code so the logic around them stays testable and the view owns presentation.
/// </summary>
public interface IUserInteraction
{
    bool Confirm(string title, string message, string confirmButtonText = "Continue");

    /// <summary>Returns null when the user cancels.</summary>
    string? PromptForText(string title, string prompt, string initialValue = "");

    /// <summary>Returns null when the user cancels.</summary>
    string? PromptForSavePath(string title, string filter, string suggestedFileName);

    /// <summary>Returns null when the user cancels.</summary>
    string? PromptForOpenPath(string title, string filter, string initialDirectory);

    void ShowError(string title, string message);

    void ShowInformation(string title, string message);
}
