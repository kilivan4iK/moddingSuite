using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using moddingSuite.ViewModel.Edata;

namespace moddingSuite.View.Edata
{
    public partial class EdataManagerView : Window
    {
        private EdataManagerViewModel _wiredVm;
        // Captured once after InitializeComponent so "Reset panel layout" can
        // restore the original positions even after a user has dragged a
        // pane somewhere unrecoverable.
        private string _defaultLayoutXml;

        public EdataManagerView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoadedCaptureLayout;
        }

        private void OnLoadedCaptureLayout(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoadedCaptureLayout;
            if (DockManager == null) return;
            try
            {
                var serializer = new XmlLayoutSerializer(DockManager);
                using var sw = new StringWriter();
                serializer.Serialize(sw);
                _defaultLayoutXml = sw.ToString();
            }
            catch { /* best effort — Reset will fall back to Show() */ }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_wiredVm != null)
            {
                _wiredVm.ToggleAnchorableRequested -= ToggleAnchorable;
                _wiredVm.ResetPaneLayoutRequested  -= ResetPaneLayout;
                _wiredVm.FocusDocumentRequested    -= FocusDocument;
                _wiredVm = null;
            }
            if (e.NewValue is EdataManagerViewModel vm)
            {
                _wiredVm = vm;
                vm.ToggleAnchorableRequested += ToggleAnchorable;
                vm.ResetPaneLayoutRequested  += ResetPaneLayout;
                vm.FocusDocumentRequested    += FocusDocument;
            }
        }

        private void FocusDocument(EdataFileViewModel target)
        {
            if (target == null || DockManager?.Layout == null) return;
            var doc = DockManager.Layout
                .Descendents()
                .OfType<LayoutDocument>()
                .FirstOrDefault(d => ReferenceEquals(d.Content, target));
            if (doc != null) doc.IsActive = true;
        }

        private void ToggleAnchorable(string contentId)
        {
            if (string.IsNullOrEmpty(contentId) || DockManager?.Layout == null) return;

            var anchorable = DockManager.Layout
                .Descendents()
                .OfType<LayoutAnchorable>()
                .FirstOrDefault(a => string.Equals(a.ContentId, contentId, StringComparison.OrdinalIgnoreCase));
            if (anchorable == null) return;

            if (anchorable.IsHidden || !anchorable.IsVisible) anchorable.Show();
            else anchorable.Hide();
        }

        private void ResetPaneLayout()
        {
            if (DockManager?.Layout == null) return;

            // Preferred path: restore the exact layout captured at first Loaded.
            if (!string.IsNullOrEmpty(_defaultLayoutXml))
            {
                try
                {
                    var serializer = new XmlLayoutSerializer(DockManager);
                    using var sr = new StringReader(_defaultLayoutXml);
                    serializer.Deserialize(sr);
                    return;
                }
                catch { /* fall through to Show()-everything fallback */ }
            }

            foreach (var a in DockManager.Layout.Descendents().OfType<LayoutAnchorable>())
                a.Show();
        }

        private void UnifiedZzTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is EdataManagerViewModel vm)
                vm.SelectedUnifiedZzNode = e.NewValue as VirtualNodeViewModel;
        }

        // Right-pane list view double-click: open file or drill into folder
        // (re-uses the same command the tree's tile double-click invokes).
        private void ZzListItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem item &&
                item.DataContext is VirtualNodeViewModel node &&
                DataContext is EdataManagerViewModel vm)
            {
                if (node.IsFolder)
                    vm.SelectedUnifiedZzNode = node;
                else if (vm.OpenUnifiedZzNodeCommand?.CanExecute(node) == true)
                    vm.OpenUnifiedZzNodeCommand.Execute(node);
            }
        }
    }
}
