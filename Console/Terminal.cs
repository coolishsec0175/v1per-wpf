namespace v1per_wpf;

/// <summary>
/// User-input prompts for long-running commands. In the GUI build these are
/// modal dialogs (see <see cref="Dialogs"/>); commands just await the result.
/// </summary>
public static class Terminal
{
    /// <summary>Shows a modal text prompt and returns the typed line.</summary>
    public static Task<string> PromptAsync(string prompt) => Dialogs.PromptAsync(prompt);

    /// <summary>Yes/no confirmation prompt.</summary>
    public static Task<bool> ConfirmAsync(string prompt) => Dialogs.ConfirmAsync(prompt);
}