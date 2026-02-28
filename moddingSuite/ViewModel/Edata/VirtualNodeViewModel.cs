using System.Collections.ObjectModel;
using System;
using moddingSuite.BL.Edata.Model;
using moddingSuite.ViewModel.Base;

namespace moddingSuite.ViewModel.Edata
{
    public class VirtualNodeViewModel : ViewModelBase
    {
        private readonly ObservableCollection<VirtualNodeViewModel> _children = new ObservableCollection<VirtualNodeViewModel>();
        private int _fileCount;
        private bool _isMultiSelected;
        private bool _isExpanded;

        public VirtualNodeViewModel(string name, string relativePath, bool isFolder)
        {
            Name = name;
            RelativePath = relativePath;
            IsFolder = isFolder;
        }

        public string Name { get; private set; }

        public string RelativePath { get; private set; }

        public bool IsFolder { get; private set; }

        public UnifiedZzEntry Entry { get; set; }

        public MergeKind? MergeKind { get; set; }

        public bool IsMultiSelected
        {
            get { return _isMultiSelected; }
            set
            {
                _isMultiSelected = value;
                OnPropertyChanged();
            }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }

        public bool IsNdfbin
        {
            get { return !IsFolder && Name.EndsWith(".ndfbin", StringComparison.OrdinalIgnoreCase); }
        }

        public ObservableCollection<VirtualNodeViewModel> Children
        {
            get { return _children; }
        }

        public int FileCount
        {
            get { return _fileCount; }
            set
            {
                _fileCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public string DisplayName
        {
            get
            {
                if (IsFolder)
                    return FileCount > 0 ? string.Format("{0} ({1})", Name, FileCount) : Name;

                string modeMarker = MergeKind == moddingSuite.BL.Edata.Model.MergeKind.Concatenate ? "[concat]" : "[latest]";
                return string.Format("{0} {1}", Name, modeMarker);
            }
        }
    }
}
