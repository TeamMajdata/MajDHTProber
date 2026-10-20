using System;
using Avalonia.Controls;
using MajDHTProber.ViewModels;

#nullable enable

namespace MajDHTProber.Views
{
    /// <summary>The log page; scrolls to the newest event while the view model asks it to follow the feed.</summary>
    public partial class LogsView : UserControl
    {
        /// <summary>The view model whose scroll requests are subscribed.</summary>
        private LogsViewModel? _subscribed;

        /// <summary>Creates the view.</summary>
        public LogsView()
        {
            InitializeComponent();
        }

        /// <summary>Re-subscribes to scroll requests when the view model changes.</summary>
        /// <param name="e">The event data.</param>
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_subscribed != null)
            {
                _subscribed.ScrollToEndRequested -= OnScrollToEndRequested;
            }
            _subscribed = DataContext as LogsViewModel;
            if (_subscribed != null)
            {
                _subscribed.ScrollToEndRequested += OnScrollToEndRequested;
                OnScrollToEndRequested(this, EventArgs.Empty);
            }
        }

        /// <summary>Stops listening when the view leaves the visual tree.</summary>
        /// <param name="e">The event data.</param>
        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            if (_subscribed != null)
            {
                _subscribed.ScrollToEndRequested -= OnScrollToEndRequested;
                _subscribed = null;
            }
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Re-subscribes when the view returns to the visual tree.</summary>
        /// <param name="e">The event data.</param>
        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_subscribed == null && DataContext is LogsViewModel viewModel)
            {
                _subscribed = viewModel;
                _subscribed.ScrollToEndRequested += OnScrollToEndRequested;
                OnScrollToEndRequested(this, EventArgs.Empty);
            }
        }

        /// <summary>Scrolls the newest event into view.</summary>
        /// <param name="sender">The view model.</param>
        /// <param name="e">The ignored event data.</param>
        private void OnScrollToEndRequested(object? sender, EventArgs e)
        {
            var count = EntryList.ItemCount;
            if (count > 0)
            {
                EntryList.ScrollIntoView(count - 1);
            }
        }
    }
}
