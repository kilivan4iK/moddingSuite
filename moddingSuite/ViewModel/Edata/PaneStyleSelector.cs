using System.Windows;
using System.Windows.Controls;
using AvalonDock.Controls;

namespace moddingSuite.ViewModel.Edata
{
    // Picks the right LayoutItem container style depending on whether
    // AvalonDock is hosting a document tab (open .dat file) or an
    // anchorable side panel (Workspace / Gamespace / ZZ Virtual).
    public class PaneStyleSelector : StyleSelector
    {
        public Style DocumentStyle    { get; set; }
        public Style AnchorableStyle  { get; set; }

        public override Style SelectStyle(object item, DependencyObject container)
        {
            if (item is LayoutDocumentItem)    return DocumentStyle;
            if (item is LayoutAnchorableItem)  return AnchorableStyle;
            return base.SelectStyle(item, container);
        }
    }
}
