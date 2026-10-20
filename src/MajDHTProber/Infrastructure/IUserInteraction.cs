using System.Threading.Tasks;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>Gives view models access to file pickers, the clipboard and the theme without depending on the UI framework.</summary>
    public interface IUserInteraction
    {
        /// <summary>Asks the user to choose an existing file.</summary>
        /// <param name="title">The picker title.</param>
        /// <returns>The local file path, or null when cancelled.</returns>
        Task<string?> PickOpenFileAsync(string title);

        /// <summary>Asks the user to choose a destination file.</summary>
        /// <param name="title">The picker title.</param>
        /// <param name="suggestedName">The suggested file name.</param>
        /// <returns>The local file path, or null when cancelled.</returns>
        Task<string?> PickSaveFileAsync(string title, string suggestedName);

        /// <summary>Copies text to the clipboard.</summary>
        /// <param name="text">The text.</param>
        /// <returns>Completion after copying.</returns>
        Task CopyToClipboardAsync(string text);

        /// <summary>Applies a theme: <c>Default</c>, <c>Light</c> or <c>Dark</c>.</summary>
        /// <param name="theme">The theme name.</param>
        void ApplyTheme(string theme);

        /// <summary>Gets the effective theme, <c>Light</c> or <c>Dark</c>.</summary>
        /// <returns>The effective theme name.</returns>
        string GetEffectiveTheme();
    }

    /// <summary>A user interaction that does nothing, for tests and design-time previews.</summary>
    public sealed class NullUserInteraction : IUserInteraction
    {
        /// <summary>Gets the shared instance.</summary>
        public static NullUserInteraction Instance { get; } = new NullUserInteraction();

        /// <summary>Gets the text most recently copied to the clipboard.</summary>
        public string? Clipboard { get; private set; }

        /// <summary>Gets the most recently applied theme.</summary>
        public string Theme { get; private set; } = "Default";

        /// <summary>Returns no file.</summary>
        /// <param name="title">The ignored title.</param>
        /// <returns>Null.</returns>
        public Task<string?> PickOpenFileAsync(string title)
        {
            return Task.FromResult<string?>(null);
        }

        /// <summary>Returns no file.</summary>
        /// <param name="title">The ignored title.</param>
        /// <param name="suggestedName">The ignored file name.</param>
        /// <returns>Null.</returns>
        public Task<string?> PickSaveFileAsync(string title, string suggestedName)
        {
            return Task.FromResult<string?>(null);
        }

        /// <summary>Remembers the text.</summary>
        /// <param name="text">The text.</param>
        /// <returns>A completed task.</returns>
        public Task CopyToClipboardAsync(string text)
        {
            Clipboard = text;
            return Task.CompletedTask;
        }

        /// <summary>Remembers the theme.</summary>
        /// <param name="theme">The theme name.</param>
        public void ApplyTheme(string theme)
        {
            Theme = theme;
        }

        /// <summary>Returns the remembered theme, treating <c>Default</c> as light.</summary>
        /// <returns>The effective theme name.</returns>
        public string GetEffectiveTheme()
        {
            return Theme == "Dark" ? "Dark" : "Light";
        }
    }
}
