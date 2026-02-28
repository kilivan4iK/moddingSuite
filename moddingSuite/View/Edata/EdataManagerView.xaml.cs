using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using moddingSuite.ViewModel.Edata;

namespace moddingSuite.View.Edata
{
    /// <summary>
    /// Interaction logic for EdataManagerView.xaml
    /// </summary>
    public partial class EdataManagerView : Window
    {
        public EdataManagerView()
        {
            InitializeComponent();
        }

        private void UnifiedZzTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is EdataManagerViewModel vm)
                vm.SelectedUnifiedZzNode = e.NewValue as VirtualNodeViewModel;
        }
    }
}
