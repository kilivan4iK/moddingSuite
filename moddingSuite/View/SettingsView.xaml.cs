using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.IO;
using moddingSuite.BL;
using moddingSuite.Model.Settings;

namespace moddingSuite.View
{
    /// <summary>
    /// Interaktionslogik für SettingsView.xaml
    /// </summary>
    public partial class SettingsView : Window
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        private void SaveButtonClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CanceButtonClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void WorkSpaceBrowserButtonClick(object sender, RoutedEventArgs e)
        {
            var settings = DataContext as Settings;
            
            if (settings == null)
                return;

            var folderDlg = new FolderBrowserDialog
            {
                SelectedPath = settings.SavePath,
                //RootFolder = Environment.SpecialFolder.MyComputer,
                ShowNewFolderButton = true,
            };

            if (folderDlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                settings.SavePath = folderDlg.SelectedPath;
        }

        private void GameSpaceButtonClick(object sender, RoutedEventArgs e)
        {
            var settings = DataContext as Settings;

            if (settings == null)
                return;

            var folderDlg = new FolderBrowserDialog
            {
                SelectedPath = settings.WargamePath,
                //RootFolder = Environment.SpecialFolder.MyComputer,
                ShowNewFolderButton = true,
            };

            if (folderDlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                settings.WargamePath = folderDlg.SelectedPath;
        }

        private void QuickBmsExeButtonClick(object sender, RoutedEventArgs e)
        {
            var settings = DataContext as Settings;
            if (settings == null)
                return;

            var openDlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "QuickBMS executable|quickbms*.exe|Executable files|*.exe|All files|*.*",
                Multiselect = false
            };

            string initialDirectory = ResolveInitialDirectory(settings.QuickBmsPath);
            if (!string.IsNullOrWhiteSpace(initialDirectory))
                openDlg.InitialDirectory = initialDirectory;

            if (openDlg.ShowDialog().GetValueOrDefault())
                settings.QuickBmsPath = openDlg.FileName;
        }

        private void QuickBmsScriptButtonClick(object sender, RoutedEventArgs e)
        {
            var settings = DataContext as Settings;
            if (settings == null)
                return;

            var openDlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "QuickBMS script|*.bms|All files|*.*",
                Multiselect = false
            };

            string initialDirectory = ResolveInitialDirectory(settings.QuickBmsScriptPath);
            if (!string.IsNullOrWhiteSpace(initialDirectory))
                openDlg.InitialDirectory = initialDirectory;

            if (openDlg.ShowDialog().GetValueOrDefault())
                settings.QuickBmsScriptPath = openDlg.FileName;
        }

        private static string ResolveInitialDirectory(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                return null;

            if (Directory.Exists(configuredPath))
                return configuredPath;

            if (File.Exists(configuredPath))
                return System.IO.Path.GetDirectoryName(configuredPath);

            return null;
        }
    }
}
