using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using moddingSuite.BL;
using moddingSuite.Model.Edata;
using moddingSuite.ViewModel.Base;

namespace moddingSuite.ViewModel.Edata
{
    public class EdataFileViewModel : ViewModelBase
    {
        private ObservableCollection<EdataContentFile> _files;
        private readonly ObservableCollection<EdataContentFile> _selectedFiles = new ObservableCollection<EdataContentFile>();
        private ICollectionView _filesCollectionView;
        private string _filterExpression = string.Empty;
        private string _loadedFile = string.Empty;
        private EdataManagerViewModel _parentVm;

        public EdataManager EdataManager { get; protected set; }

        public ICommand CloseCommand { get; set; }

        public ICommand DetailsCommand { get; set; }

        public EdataManagerViewModel ParentVm
        {
            get
            {
                return _parentVm;
            }
        }

        public EdataFileViewModel(EdataManagerViewModel parentVm)
        {
            _parentVm = parentVm;

            CloseCommand = new ActionCommand((x) => ParentVm.CloseFile(this));
            DetailsCommand = new ActionCommand(DetailsExecute);
        }

        private void DetailsExecute(object obj)
        {
            var file = obj as EdataContentFile;

            if (file == null)
                return;

            var fileType = file.FileType;
            if (fileType == EdataFileType.Unknown)
                fileType = EdataManager.GetFileTypeFromFileName(file.Name);

            switch (fileType)
            {
                case EdataFileType.Ndfbin:
                    ParentVm.EditNdfbinCommand.Execute(obj);
                    break;
                case EdataFileType.Image:
                    ParentVm.ExportTextureCommand.Execute(obj);
                    break;
                case EdataFileType.Dictionary:
                    ParentVm.EditTradFileCommand.Execute(obj);
                    break;
                case EdataFileType.Mesh:
                    ParentVm.EditMeshCommand.Execute(obj);
                    break;
                case EdataFileType.Scenario:
                    ParentVm.EditScenarioCommand.Execute(obj);
                    break;
                case EdataFileType.Package:
                    ParentVm.OpenNestedPackage(file, this);
                    break;
                case EdataFileType.Video:
                    ParentVm.PlayMovieCommand.Execute(obj);
                    break;
                default:
                    if (file.Name.EndsWith(".ess", StringComparison.OrdinalIgnoreCase))
                        ParentVm.ExportSoundCommand.Execute(obj);
                    break;
            }
        }

        public string LoadedFile
        {
            get { return _loadedFile; }
            set
            {
                _loadedFile = value;
                OnPropertyChanged(() => LoadedFile);
                OnPropertyChanged(() => HeaderText);
            }
        }

        public string HeaderText
        {
            get
            {
                var f = new FileInfo(LoadedFile);

                return f.Name;
            }
        }

        public ObservableCollection<EdataContentFile> Files
        {
            get { return _files; }
            set
            {
                _files = value;
                OnPropertyChanged(() => Files);
            }
        }

        public ObservableCollection<EdataContentFile> SelectedFiles
        {
            get { return _selectedFiles; }
        }

        public ICollectionView FilesCollectionView
        {
            get
            {
                if (_filesCollectionView == null)
                {
                    CreateFilesCollectionView();
                }

                return _filesCollectionView;
            }
        }

        public string FilterExpression
        {
            get { return _filterExpression; }
            set
            {
                _filterExpression = value;
                OnPropertyChanged(() => FilterExpression);
                FilesCollectionView.Refresh();
            }
        }

        public void LoadFile(string path)
        {
            EdataManager = new EdataManager(path);

            LoadedFile = EdataManager.FilePath;

            EdataManager.ParseEdataFile();
            Files = EdataManager.Files;
            _selectedFiles.Clear();
            CreateFilesCollectionView();
        }

        public void SetSelectedFiles(IEnumerable<EdataContentFile> selectedFiles)
        {
            _selectedFiles.Clear();

            if (selectedFiles == null)
                return;

            foreach (EdataContentFile file in selectedFiles)
            {
                if (file != null)
                    _selectedFiles.Add(file);
            }
        }

        public bool FilterPath(object item)
        {
            var file = item as EdataContentFile;

            if (file == null || FilterExpression == string.Empty || FilterExpression.Length < 3)
            {
                return true;
            }

            return file.Path.Contains(FilterExpression);
        }

        private void CreateFilesCollectionView()
        {
            _filesCollectionView = CollectionViewSource.GetDefaultView(Files);
            _filesCollectionView.Filter = FilterPath;

            OnPropertyChanged(() => FilesCollectionView);
        }
    }
}
