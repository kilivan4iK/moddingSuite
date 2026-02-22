using System.Linq;
using System.Windows.Controls;
using moddingSuite.Model.Edata;
using moddingSuite.ViewModel.Edata;

namespace moddingSuite.View.Edata
{
    /// <summary>
    /// Interaction logic for EdataFileView.xaml
    /// </summary>
    public partial class EdataFileView : UserControl
    {
        public EdataFileView()
        {
            InitializeComponent();
        }

        private void DataGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var vm = DataContext as EdataFileViewModel;
            if (vm == null)
                return;

            vm.SetSelectedFiles(DataGrid.SelectedItems.OfType<EdataContentFile>());
        }
    }
}
