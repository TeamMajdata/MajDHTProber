using MajDHTProber.Infrastructure;
using MajDHTProber.Services;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>The services shared by every page view model.</summary>
    public sealed class ProberContext
    {
        /// <summary>Creates the context.</summary>
        /// <param name="manager">The hosted node sessions.</param>
        /// <param name="dispatcher">Marshals work onto the UI thread.</param>
        /// <param name="interaction">File pickers, clipboard and theme.</param>
        /// <param name="notifications">Transient notifications.</param>
        public ProberContext(NodeSessionManager manager, IUiDispatcher dispatcher, IUserInteraction interaction, NotificationCenter notifications)
        {
            Manager = manager;
            Dispatcher = dispatcher;
            Interaction = interaction;
            Notifications = notifications;
        }

        /// <summary>Gets the hosted node sessions.</summary>
        public NodeSessionManager Manager { get; }

        /// <summary>Gets the UI dispatcher.</summary>
        public IUiDispatcher Dispatcher { get; }

        /// <summary>Gets file pickers, clipboard and theme.</summary>
        public IUserInteraction Interaction { get; }

        /// <summary>Gets the transient notifications.</summary>
        public NotificationCenter Notifications { get; }

        /// <summary>Gets the session operations act on.</summary>
        public NodeSession ActiveSession => Manager.ActiveSession;
    }
}
