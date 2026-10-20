using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>Implements user interaction with Avalonia storage pickers, clipboard and theme variants.</summary>
    public sealed class AvaloniaUserInteraction : IUserInteraction
    {
        /// <summary>Returns the top level hosting the pickers, once the main window exists.</summary>
        private readonly Func<TopLevel?> _topLevel;

        /// <summary>Creates the interaction.</summary>
        /// <param name="topLevel">Returns the top level hosting the pickers.</param>
        public AvaloniaUserInteraction(Func<TopLevel?> topLevel)
        {
            _topLevel = topLevel;
        }

        /// <summary>Asks the user to choose an existing file.</summary>
        /// <param name="title">The picker title.</param>
        /// <returns>The local file path, or null when cancelled or unavailable.</returns>
        public async Task<string?> PickOpenFileAsync(string title)
        {
            var storage = _topLevel()?.StorageProvider;
            if (storage == null || !storage.CanOpen)
            {
                return null;
            }
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        }

        /// <summary>Asks the user to choose a destination file.</summary>
        /// <param name="title">The picker title.</param>
        /// <param name="suggestedName">The suggested file name.</param>
        /// <returns>The local file path, or null when cancelled or unavailable.</returns>
        public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
        {
            var storage = _topLevel()?.StorageProvider;
            if (storage == null || !storage.CanSave)
            {
                return null;
            }
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName });
            return file?.TryGetLocalPath();
        }

        /// <summary>Copies text to the clipboard.</summary>
        /// <param name="text">The text.</param>
        /// <returns>Completion after copying.</returns>
        public async Task CopyToClipboardAsync(string text)
        {
            var clipboard = _topLevel()?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }

        /// <summary>Applies a theme variant to the application.</summary>
        /// <param name="theme">The theme name: <c>Default</c>, <c>Light</c> or <c>Dark</c>.</param>
        public void ApplyTheme(string theme)
        {
            if (Application.Current == null)
            {
                return;
            }
            Application.Current.RequestedThemeVariant = theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        /// <summary>Gets the effective theme variant.</summary>
        /// <returns><c>Dark</c> or <c>Light</c>.</returns>
        public string GetEffectiveTheme()
        {
            return Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? "Dark" : "Light";
        }
    }
}
