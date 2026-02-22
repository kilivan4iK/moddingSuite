using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using moddingSuite.Model.Settings;

namespace moddingSuite.ViewModel.Edata
{
    public class GameSpaceViewModel : FileSystemOverviewViewModelBase
    {
        private readonly ObservableCollection<DirectoryViewModel> _displayRoot = new ObservableCollection<DirectoryViewModel>();
        private bool _showOnlyZzDatFiles;

        public GameSpaceViewModel(Settings settings)
        {
            RootPath = settings.WargamePath;

            if (Directory.Exists(RootPath))
                Root.Add(ParseRoot());

            RefreshDisplayRoot();
        }

        public ObservableCollection<DirectoryViewModel> DisplayRoot
        {
            get { return _displayRoot; }
        }

        public bool ShowOnlyZzDatFiles
        {
            get { return _showOnlyZzDatFiles; }
            set
            {
                if (_showOnlyZzDatFiles == value)
                    return;

                _showOnlyZzDatFiles = value;
                OnPropertyChanged();
                RefreshDisplayRoot();
            }
        }

        private void RefreshDisplayRoot()
        {
            _displayRoot.Clear();

            foreach (DirectoryViewModel rootDirectory in Root)
            {
                DirectoryViewModel copy = ShowOnlyZzDatFiles
                    ? CloneDirectoryWithZzFilter(rootDirectory)
                    : rootDirectory;

                if (copy != null)
                    _displayRoot.Add(copy);
            }
        }

        private static DirectoryViewModel CloneDirectoryWithZzFilter(DirectoryViewModel source)
        {
            var clone = new DirectoryViewModel(source.Info);

            foreach (FileSystemItemViewModel item in source.Items)
            {
                var childDirectory = item as DirectoryViewModel;
                if (childDirectory != null)
                {
                    DirectoryViewModel filteredChild = CloneDirectoryWithZzFilter(childDirectory);
                    if (filteredChild != null)
                        clone.Items.Add(filteredChild);

                    continue;
                }

                var childFile = item as FileViewModel;
                if (childFile != null && IsNumberedZzDatFile(childFile.Info.Name))
                    clone.Items.Add(new FileViewModel(childFile.Info));
            }

            if (clone.Items.Count == 0)
                return null;

            return clone;
        }

        private static bool IsNumberedZzDatFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            if (!fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                return false;

            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            if (!nameWithoutExtension.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = nameWithoutExtension.Substring(3);
            return suffix.Length > 0 && suffix.All(char.IsDigit);
        }
    }
}
