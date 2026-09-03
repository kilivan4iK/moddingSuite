using System.Collections.ObjectModel;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using moddingSuite.BL.Edata.Model;
using moddingSuite.BL.TGV;
using moddingSuite.ViewModel.Base;

namespace moddingSuite.ViewModel.Edata
{
    public class VirtualNodeViewModel : ViewModelBase
    {
        private readonly ObservableCollection<VirtualNodeViewModel> _children = new ObservableCollection<VirtualNodeViewModel>();
        private int _fileCount;
        private bool _isMultiSelected;
        private bool _isExpanded;
        private BitmapSource _thumbnail;
        private bool _thumbnailRequested;

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
            get
            {
                return !IsFolder &&
                       (Name.EndsWith(".ndfbin", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool IsTexture
        {
            get
            {
                return !IsFolder && Name != null &&
                       (Name.EndsWith(".tgv", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool IsAudio
        {
            get
            {
                return !IsFolder && Name != null &&
                       (Name.EndsWith(".ess", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool IsMesh
        {
            get
            {
                return !IsFolder && Name != null &&
                       (Name.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".meshpack", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool IsPython
        {
            get
            {
                return !IsFolder && Name != null &&
                       Name.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsPackage
        {
            get
            {
                return !IsFolder && Name != null &&
                       (Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                        Name.EndsWith(".zz", StringComparison.OrdinalIgnoreCase));
            }
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

        // True for files we can render an inline thumbnail for (currently .tgv).
        public bool CanHaveThumbnail
        {
            get
            {
                return !IsFolder &&
                       Name != null &&
                       Name.EndsWith(".tgv", StringComparison.OrdinalIgnoreCase) &&
                       Entry?.EffectiveOccurrence?.EntryRef != null &&
                       Entry.EffectiveOccurrence.Manager != null;
            }
        }

        // Lazily fires the async decode the first time WPF binds to this property.
        // OnPropertyChanged refires the binding once decoding completes.
        public BitmapSource Thumbnail
        {
            get
            {
                if (_thumbnail == null && !_thumbnailRequested && CanHaveThumbnail)
                {
                    _thumbnailRequested = true;
                    StartThumbnailLoad();
                }
                return _thumbnail;
            }
        }

        private void StartThumbnailLoad()
        {
            ZzFileOccurrence src = Entry?.EffectiveOccurrence;
            if (src?.Manager == null || src.EntryRef == null) return;

            // Stable cache key: archive on disk + virtual path within that archive.
            string key = (src.ArchivePath ?? "") + "|" + (src.VirtualPath ?? Name);
            System.Windows.Threading.Dispatcher uiDispatcher =
                Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

            _ = Task.Run(async () =>
            {
                try
                {
                    BitmapSource img = await ZzThumbnailCache.Instance.GetOrLoadAsync(
                        key,
                        () => src.Manager.GetRawData(src.EntryRef),
                        CancellationToken.None);
                    if (img != null)
                    {
                        uiDispatcher.BeginInvoke(new Action(() =>
                        {
                            _thumbnail = img;
                            OnPropertyChanged(nameof(Thumbnail));
                            OnPropertyChanged(nameof(HasThumbnail));
                        }));
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Thumbnail load failed for {0}: {1}", Name, ex.Message);
                }
            });
        }

        public bool HasThumbnail
        {
            get { return _thumbnail != null; }
        }
    }
}
