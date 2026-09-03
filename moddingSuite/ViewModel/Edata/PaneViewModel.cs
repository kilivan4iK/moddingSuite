using moddingSuite.ViewModel.Base;

namespace moddingSuite.ViewModel.Edata
{
    // Anchorable panel wrappers used by AvalonDock's AnchorablesSource binding.
    // Each pane carries a Title for the tab header and a reference back to the
    // root manager so the per-pane DataTemplate can bind to its data (Workspace
    // root, Gamespace root, ZZ virtual nodes, etc.) without losing DataContext
    // when the user floats/docks the panel into a separate window.
    public abstract class PaneViewModel : ViewModelBase
    {
        private bool _isVisible = true;

        public string Title { get; set; }
        public string ContentId { get; set; }
        public EdataManagerViewModel Owner { get; set; }

        // Two-way bound to LayoutAnchorableItem.IsVisible via the container
        // style. Flipping this from the Window menu makes AvalonDock show or
        // hide the pane; closing a pane from its X button flips this back.
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public sealed class WorkspacePaneViewModel : PaneViewModel { }
    public sealed class GamespacePaneViewModel : PaneViewModel { }
    public sealed class ZzVirtualPaneViewModel : PaneViewModel { }
}
