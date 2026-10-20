using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MajDHTProber.Infrastructure;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>
    /// The base class of view models that run node operations: tracks a busy flag and reports outcomes as notifications.
    /// </summary>
    public abstract partial class OperationViewModel : ViewModelBase
    {
        /// <summary>Creates the view model.</summary>
        /// <param name="context">The shared services.</param>
        protected OperationViewModel(ProberContext context)
        {
            Context = context;
        }

        /// <summary>Gets or sets whether an operation is running.</summary>
        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        /// <summary>Gets the shared services.</summary>
        protected ProberContext Context { get; }

        /// <summary>Gets the session operations act on.</summary>
        protected NodeSession Session => Context.ActiveSession;

        /// <summary>Shows a notification.</summary>
        /// <param name="message">The text.</param>
        /// <param name="tone">The tone.</param>
        protected void Notify(string message, StatusTone tone = StatusTone.Info)
        {
            Context.Notifications.Show(message, tone);
        }

        /// <summary>Runs an operation, reporting failures as notifications instead of throwing.</summary>
        /// <param name="action">The user-facing action name used in failure messages.</param>
        /// <param name="work">The operation.</param>
        /// <returns>Whether the operation completed without an exception.</returns>
        protected async Task<bool> RunAsync(string action, Func<Task> work)
        {
            IsBusy = true;
            try
            {
                await work();
                return true;
            }
            catch (OperationCanceledException)
            {
                Notify(action + "已取消。", StatusTone.Neutral);
                return false;
            }
            catch (Exception exception)
            {
                Notify(action + "失败：" + Display.Error(exception), StatusTone.Danger);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Copies text to the clipboard and confirms it.</summary>
        /// <param name="text">The text, ignored when empty.</param>
        /// <param name="what">The user-facing name of the copied value.</param>
        /// <returns>Completion after copying.</returns>
        protected async Task CopyAsync(string? text, string what)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            await Context.Interaction.CopyToClipboardAsync(text);
            Notify("已复制" + what + "。", StatusTone.Success);
        }
    }

    /// <summary>The base class of the navigation pages.</summary>
    public abstract class PageViewModel : OperationViewModel
    {
        /// <summary>Creates a page.</summary>
        /// <param name="context">The shared services.</param>
        protected PageViewModel(ProberContext context)
            : base(context)
        {
        }

        /// <summary>Called on the UI thread when the page becomes visible.</summary>
        public virtual void OnActivated()
        {
        }

        /// <summary>Called on the UI thread after the active session changed.</summary>
        public virtual void OnActiveSessionChanged()
        {
        }

        /// <summary>Called on the UI thread about once per second while the page is visible.</summary>
        /// <param name="now">The current time.</param>
        public virtual void OnTick(DateTimeOffset now)
        {
        }
    }

    /// <summary>One entry of the navigation sidebar.</summary>
    public sealed class NavigationItem
    {
        /// <summary>Creates a navigation entry.</summary>
        /// <param name="title">The page title.</param>
        /// <param name="description">The one-line page description shown in the header.</param>
        /// <param name="iconKey">The resource key of the page icon geometry.</param>
        /// <param name="page">The page view model.</param>
        public NavigationItem(string title, string description, string iconKey, PageViewModel page)
        {
            Title = title;
            Description = description;
            IconKey = iconKey;
            Page = page;
        }

        /// <summary>Gets the page title.</summary>
        public string Title { get; }

        /// <summary>Gets the one-line page description.</summary>
        public string Description { get; }

        /// <summary>Gets the resource key of the page icon geometry.</summary>
        public string IconKey { get; }

        /// <summary>Gets the page view model.</summary>
        public PageViewModel Page { get; }
    }
}
