using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using moddingSuite.BL;
using moddingSuite.BL.Ndf;
using moddingSuite.Model.Edata;
using moddingSuite.Model.Settings;
using moddingSuite.View.Common;
using moddingSuite.View.DialogProvider;
using moddingSuite.View.Ndfbin;
using moddingSuite.ViewModel.About;
using moddingSuite.ViewModel.Base;
using moddingSuite.ViewModel.Media;
using moddingSuite.ViewModel.Mesh;
using moddingSuite.ViewModel.Ndf;
using moddingSuite.ViewModel.Scenario;
using moddingSuite.ViewModel.Trad;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System.Threading.Tasks;
using System.Windows.Threading;
using moddingSuite.BL.Ess;
using moddingSuite.BL.TGV;
using moddingSuite.BL.Mesh;

namespace moddingSuite.ViewModel.Edata
{
    public class EdataManagerViewModel : ViewModelBase
    {
        private readonly ObservableCollection<EdataFileViewModel> _openFiles = new ObservableCollection<EdataFileViewModel>();
        private readonly ObservableCollection<string> _zzDatSearchResults = new ObservableCollection<string>();

        private string _statusText;
        private string _zzDatSearchQuery = string.Empty;

        public string StatusText
        {
            get { return _statusText; }
            set
            {
                _statusText = value;
                OnPropertyChanged(() => StatusText);
            }
        }

        public string ZzDatSearchQuery
        {
            get { return _zzDatSearchQuery; }
            set
            {
                _zzDatSearchQuery = value;
                OnPropertyChanged(() => ZzDatSearchQuery);
            }
        }

        public EdataManagerViewModel()
        {
            InitializeCommands();

            Settings settings = SettingsManager.Load();

            var failedFiles = new List<FileInfo>();

            foreach (var file in settings.LastOpenedFiles)
            {
                var fileInfo = new FileInfo(file);

                if (fileInfo.Exists)
                    try
                    {
                        AddFile(fileInfo.FullName);
                    }
                    catch (IOException)
                    {
                        failedFiles.Add(fileInfo);
                    }
            }

            if (failedFiles.Count > 0)
                StatusText = $"{failedFiles.Count} files failed to open. Did you start the modding suite while running the game?";

            if (settings.LastOpenedFiles.Count == 0)
                CollectionViewSource.GetDefaultView(OpenFiles).MoveCurrentToFirst();

            Workspace = new WorkspaceViewModel(settings);
            Gamespace = new GameSpaceViewModel(settings);

            OpenFiles.CollectionChanged += OpenFilesCollectionChanged;
        }

        public ICommand ExportNdfCommand { get; set; }
        public ICommand ExportRawCommand { get; set; }
        public ICommand ReplaceRawCommand { get; set; }
        public ICommand ExportTextureCommand { get; set; }
        public ICommand ReplaceTextureCommand { get; set; }
        public ICommand ExportSoundCommand { get; set; }
        public ICommand ReplaceSoundCommand { get; set; }
        public ICommand OpenFileCommand { get; set; }
        public ICommand CloseFileCommand { get; set; }
        public ICommand ChangeExportPathCommand { get; set; }
        public ICommand ChangeWargamePathCommand { get; set; }
        public ICommand ChangePythonPathCommand { get; set; }
        public ICommand EditNdfbinCommand { get; set; }
        public ICommand EditTradFileCommand { get; set; }
        public ICommand EditMeshCommand { get; set; }
        public ICommand EditScenarioCommand { get; set; }
        public ICommand PlayMovieCommand { get; set; }
        public ICommand AboutUsCommand { get; set; }
        public ICommand ReplaceRawFromWorkspaceCommand { get; set; }
        public ICommand ReplaceTextureFromWorkspaceCommand { get; set; }
        public ICommand ReplaceSoundFromWorkspaceCommand { get; set; }
        public ICommand OpenEdataFromWorkspaceCommand { get; set; }
        public ICommand AddNewFileCommand { get; set; }
        public ICommand SearchInZzDatCommand { get; set; }


        public ObservableCollection<EdataFileViewModel> OpenFiles
        {
            get { return _openFiles; }
        }

        public ObservableCollection<string> ZzDatSearchResults
        {
            get { return _zzDatSearchResults; }
        }

        public WorkspaceViewModel Workspace
        {
            get; set;
        }

        public GameSpaceViewModel Gamespace
        {
            get; set;
        }

        protected void OpenFilesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Settings set = SettingsManager.Load();
            set.LastOpenedFiles.Clear();
            set.LastOpenedFiles.AddRange(OpenFiles.Select(x => x.LoadedFile).ToList());
            SettingsManager.Save(set);
        }

        public void AddFile(string path)
        {
            var vm = new EdataFileViewModel(this);

            vm.LoadFile(path);

            OpenFiles.Add(vm);

            CollectionViewSource.GetDefaultView(OpenFiles).MoveCurrentTo(vm);
            StatusText = BuildOpenFileSummary(vm);
        }

        public void OpenNestedPackage(EdataContentFile packageFile, EdataFileViewModel ownerVm)
        {
            if (packageFile == null || ownerVm == null)
                return;

            try
            {
                byte[] packageData = ownerVm.EdataManager.GetRawData(packageFile);

                string tempRoot = Path.Combine(Path.GetTempPath(), "moddingSuite", "nested_packages");
                Directory.CreateDirectory(tempRoot);

                string nestedName = Path.GetFileName(packageFile.Name);
                if (string.IsNullOrWhiteSpace(nestedName))
                    nestedName = string.Format("nested_{0}.dat", Guid.NewGuid().ToString("N"));
                else if (string.IsNullOrWhiteSpace(Path.GetExtension(nestedName)))
                    nestedName += ".dat";

                string tempPackagePath = Path.Combine(tempRoot, string.Format("{0}_{1}", Guid.NewGuid().ToString("N"), nestedName));
                File.WriteAllBytes(tempPackagePath, packageData);

                AddFile(tempPackagePath);
            }
            catch (Exception ex)
            {
                StatusText = string.Format("Failed to open nested package '{0}': {1}", packageFile.Path, ex.Message);
                Trace.TraceError("Failed to open nested package {0}: {1}", packageFile.Path, ex);
            }
        }

        public void CloseFile(EdataFileViewModel vm)
        {
            if (!OpenFiles.Contains(vm))
                return;

            OpenFiles.Remove(vm);
        }

        protected void InitializeCommands()
        {
            OpenFileCommand = new ActionCommand(OpenFileExecute);
            CloseFileCommand = new ActionCommand(CloseFileExecute);

            ChangeExportPathCommand = new ActionCommand(ChangeExportPathExecute);
            ChangeWargamePathCommand = new ActionCommand(ChangeWargamePathExecute);
            ChangePythonPathCommand = new ActionCommand(ChangePythonPathExecute);

            ExportNdfCommand = new ActionCommand(ExportNdfExecute, () => IsOfType(EdataFileType.Ndfbin));
            ExportRawCommand = new ActionCommand(ExportRawExecute);
            ReplaceRawCommand = new ActionCommand(ReplaceRawExecute);
            ExportTextureCommand = new ActionCommand(ExportTextureExecute, () => IsOfType(EdataFileType.Image) || HasSelectedFileOfType(EdataFileType.Image));
            ReplaceTextureCommand = new ActionCommand(ReplaceTextureExecute, () => IsOfType(EdataFileType.Image));

            ExportSoundCommand = new ActionCommand(ExportSoundExecute, () => HasEnding(".ess"));
            ReplaceSoundCommand = new ActionCommand(ReplaceSoundExecute, () => HasEnding(".ess"));

            PlayMovieCommand = new ActionCommand(PlayMovieExecute);

            AboutUsCommand = new ActionCommand(AboutUsExecute);

            EditTradFileCommand = new ActionCommand(EditTradFileExecute, () => IsOfType(EdataFileType.Dictionary));
            EditNdfbinCommand = new ActionCommand(EditNdfbinExecute, () => IsOfType(EdataFileType.Ndfbin));
            EditMeshCommand = new ActionCommand(EditMeshExecute, () => IsOfType(EdataFileType.Mesh));
            EditScenarioCommand = new ActionCommand(EditScenarioExecute, () => IsOfType(EdataFileType.Scenario));

            AddNewFileCommand = new ActionCommand(AddNewFileExecute);

            ReplaceRawFromWorkspaceCommand = new ActionCommand(ReplaceRawFromWorkspaceExecute);
            ReplaceTextureFromWorkspaceCommand = new ActionCommand(ReplaceTextureFromWorkspaceExecute, () => IsOfType(EdataFileType.Image));
            ReplaceSoundFromWorkspaceCommand = new ActionCommand(ReplaceSoundFromWorkspaceExecute, () => HasEnding(".ess"));
            SearchInZzDatCommand = new ActionCommand(SearchInZzDatExecute);
        }

        private void AddNewFileExecute(object obj)
        {
            if (obj is FileViewModel)
            {
                var file = obj as FileViewModel;

                HandleNewFile(file.Info.FullName);
            }
        }

        private void EditScenarioExecute(object obj)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;
            if (vm == null)
                return;

            var scenario = vm.FilesCollectionView.CurrentItem as EdataContentFile;
            if (scenario == null)
                return;

            var dispatcher = Dispatcher.CurrentDispatcher;

            Action<ViewModelBase, ViewModelBase> open = DialogProvider.ProvideView;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, "Reading scenario...");



                    var detailsVm = new ScenarioEditorViewModel(scenario, vm);

                    dispatcher.Invoke(open, detailsVm, this);
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                    dispatcher.Invoke(report, "Ready");
                }
            });

            s.Start();
        }

        private void EditMeshExecute(object obj)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;
            if (vm == null)
                return;

            var mesh = vm.FilesCollectionView.CurrentItem as EdataContentFile;
            if (mesh == null)
                return;

            var dispatcher = Dispatcher.CurrentDispatcher;

            Action<ViewModelBase, ViewModelBase> open = DialogProvider.ProvideView;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, "Reading Mesh package...");

                    var reader = new MeshReader();
                    var meshfile = reader.Read(vm.EdataManager.GetRawData(mesh));

                    var detailsVm = new MeshEditorViewModel(meshfile);

                    dispatcher.Invoke(open, detailsVm, this);
                }
                catch (Exception ex)
                {
                    dispatcher.Invoke(report, string.Format("Mesh viewer failed: {0}", ex.Message));
                    Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                    dispatcher.Invoke(report, "Ready");
                }
            });

            s.Start();
        }

        private void ReplaceRawExecute(object obj)
        {
            Settings settings = SettingsManager.Load();

            var openfDlg = new OpenFileDialog
            {
                //DefaultExt = ".*",
                Multiselect = false,
                Filter = "All files (*.*)|*.*"
            };

            if (File.Exists(settings.LastOpenFolder))
                openfDlg.InitialDirectory = settings.LastOpenFolder;

            if (openfDlg.ShowDialog().Value)
            {
                settings.LastOpenFolder = new FileInfo(openfDlg.FileName).DirectoryName;
                SettingsManager.Save(settings);

                ReplaceRawFile(File.ReadAllBytes(openfDlg.FileName));
            }
        }

        protected void ReplaceTextureExecute(object obj)
        {
            Settings settings = SettingsManager.Load();

            var openfDlg = new OpenFileDialog
            {
                DefaultExt = ".dds",
                Multiselect = false,
                Filter = "DDS files (.dds)|*.dds"
            };

            if (File.Exists(settings.LastOpenFolder))
                openfDlg.InitialDirectory = settings.LastOpenFolder;

            if (openfDlg.ShowDialog().Value)
            {
                settings.LastOpenFolder = new FileInfo(openfDlg.FileName).DirectoryName;
                SettingsManager.Save(settings);

                ReplaceTextureFile(openfDlg.FileName);
            }
        }

        private void ReplaceSoundExecute(object obj)
        {
            Settings settings = SettingsManager.Load();

            var openfDlg = new OpenFileDialog
            {
                DefaultExt = ".wav",
                Multiselect = false,
                Filter = "WAV files (.wav)|*.wav"
            };

            if (File.Exists(settings.LastOpenFolder))
                openfDlg.InitialDirectory = settings.LastOpenFolder;

            if (openfDlg.ShowDialog().Value)
            {
                settings.LastOpenFolder = new FileInfo(openfDlg.FileName).DirectoryName;
                SettingsManager.Save(settings);

                ReplaceSoundFile(openfDlg.FileName);
            }
        }

        private void ReplaceRawFromWorkspaceExecute(object obj)
        {
            var file = obj.ToString();

            if (File.Exists(file))
            {
                ReplaceRawFile(File.ReadAllBytes(file));
            }
        }

        private void ReplaceTextureFromWorkspaceExecute(object obj)
        {
            var file = obj.ToString();

            if (File.Exists(file))
            {
                ReplaceTextureFile(file);
            }
        }

        private void ReplaceSoundFromWorkspaceExecute(object obj)
        {
            var file = obj.ToString();

            if (File.Exists(file))
            {
                ReplaceSoundFile(file);
            }
        }

        private void ReplaceRawFile(byte[] newFileData)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;
            var file = vm?.FilesCollectionView.CurrentItem as EdataContentFile;

            if (file == null)
                return;

            var dispatcher = Dispatcher.CurrentDispatcher;

            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, $"Replacing {file.Path}...");

                    vm.EdataManager.ReplaceFile(file, newFileData);
                    vm.LoadFile(vm.LoadedFile);

                    dispatcher.Invoke(report, "Ready");
                }
                catch (Exception ex)
                {
                    dispatcher.Invoke(report, $"Replacing failed {ex.Message}");
                    Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        protected void ReplaceTextureFile(string newFilePath)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;
            var destTgvFile = vm?.FilesCollectionView.CurrentItem as EdataContentFile;

            if (destTgvFile == null)
                return;

            var tgvReader = new TgvReader();
            var data = vm.EdataManager.GetRawData(destTgvFile);
            var tgv = tgvReader.Read(data);

            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, $"Replacing {destTgvFile.Path}...");

                    byte[] sourceDds = File.ReadAllBytes(newFilePath);

                    dispatcher.Invoke(report, "Converting DDS to TGV file format...");

                    var ddsReader = new TgvDDSReader();
                    var sourceTgvFile = ddsReader.ReadDDS(sourceDds);
                    byte[] sourceTgvRawData;

                    using (var tgvwriterStream = new MemoryStream())
                    {
                        var tgvWriter = new TgvWriter();
                        tgvWriter.Write(tgvwriterStream, sourceTgvFile, tgv.SourceChecksum, tgv.IsCompressed);
                        sourceTgvRawData = tgvwriterStream.ToArray();
                    }

                    dispatcher.Invoke(report, "Replacing file in edata container...");

                    vm.EdataManager.ReplaceFile(destTgvFile, sourceTgvRawData);

                    vm.LoadFile(vm.LoadedFile);
                    dispatcher.Invoke(report, "Ready");
                }
                catch (Exception ex)
                {
                    dispatcher.Invoke(report, $"Replacing failed {ex.Message}");
                    Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        protected void ReplaceSoundFile(string newFilePath)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            if (vm == null)
                return;

            var file = vm.FilesCollectionView.CurrentItem as EdataContentFile;

            if (file == null)
                return;

            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, $"Replacing {file.Path}...");
                    byte[] replacefile = File.ReadAllBytes(newFilePath);

                    EssWriter writer = new EssWriter();

                    try
                    {
                        replacefile = writer.Write(replacefile);
                        vm.EdataManager.ReplaceFile(file, replacefile);
                        vm.LoadFile(vm.LoadedFile);
                        dispatcher.Invoke(report, "Ready");
                    }
                    catch (InvalidDataException ex)
                    {
                        dispatcher.Invoke(report, ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        protected void ExportTextureExecute(object obj)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            if (vm == null)
                return;

            var selectedImageFiles = vm.SelectedFiles
                .Where(IsImageFileForTextureExport)
                .Distinct()
                .ToList();

            if (selectedImageFiles.Count == 0)
            {
                var currentFile = vm.FilesCollectionView.CurrentItem as EdataContentFile;
                if (IsImageFileForTextureExport(currentFile))
                    selectedImageFiles.Add(currentFile);
            }

            if (selectedImageFiles.Count == 0)
            {
                StatusText = "Select one or more .tgv texture files to export.";
                return;
            }

            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);

                    Settings settings = SettingsManager.Load();
                    int exported = 0;
                    int failed = 0;
                    string firstFailure = null;

                    var tgvReader = new TgvReader();
                    var writer = new TgvDDSWriter();

                    for (int i = 0; i < selectedImageFiles.Count; i++)
                    {
                        EdataContentFile sourceTgvFile = selectedImageFiles[i];

                        try
                        {
                            var f = new FileInfo(sourceTgvFile.Path);
                            var exportPath = Path.Combine(settings.SavePath, f.Name + ".dds");
                            var exportDir = Path.GetDirectoryName(exportPath);
                            if (!string.IsNullOrWhiteSpace(exportDir) && !Directory.Exists(exportDir))
                                Directory.CreateDirectory(exportDir);

                            dispatcher.Invoke(report, string.Format("Exporting texture {0} ({1}/{2})...", sourceTgvFile.Path, i + 1, selectedImageFiles.Count));

                            var tgv = tgvReader.Read(vm.EdataManager.GetRawData(sourceTgvFile));
                            byte[] content = writer.CreateDDSFile(tgv);

                            using (var fs = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                fs.Write(content, 0, content.Length);
                                fs.Flush();
                            }

                            exported++;
                        }
                        catch (Exception fileEx)
                        {
                            failed++;
                            if (string.IsNullOrWhiteSpace(firstFailure))
                                firstFailure = string.Format("{0}: {1}", sourceTgvFile.Path, fileEx.Message);
                            Trace.TraceError("Texture export failed for {0}: {1}", sourceTgvFile.Path, fileEx);
                        }
                    }

                    if (failed > 0 && !string.IsNullOrWhiteSpace(firstFailure))
                        dispatcher.Invoke(report, string.Format("Texture export complete. Exported: {0}, failed: {1}. First error: {2}", exported, failed, firstFailure));
                    else
                        dispatcher.Invoke(report, string.Format("Texture export complete. Exported: {0}, failed: {1}.", exported, failed));
                }
                catch (Exception ex)
                {
                    dispatcher.Invoke(report, string.Format("Texture export failed: {0}", ex.Message));
                    Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        private void ExportSoundExecute(object obj)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            if (vm == null)
                return;

            var sourceEssFile = vm.FilesCollectionView.CurrentItem as EdataContentFile;

            if (sourceEssFile == null)
                return;

            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);

                    Settings settings = SettingsManager.Load();

                    var f = new FileInfo(sourceEssFile.Path);
                    var exportPath = Path.Combine(settings.SavePath, f.Name + ".wav");

                    dispatcher.Invoke(report, string.Format("Exporting to {0}...", exportPath));

                    var tgvReader = new EssReader();
                    var tgv = tgvReader.ReadEss(vm.EdataManager.GetRawData(sourceEssFile));

                    using (var fs = new FileStream(Path.Combine(settings.SavePath, f.Name + ".wav"), FileMode.OpenOrCreate))
                    {
                        fs.Write(tgv, 0, tgv.Length);
                        fs.Flush();
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                }
                finally
                {
                    dispatcher.Invoke(report, "Ready");
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        protected bool IsOfType(EdataFileType type)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            var ndf = vm?.FilesCollectionView.CurrentItem as EdataContentFile;

            if (ndf == null)
                return false;

            var fileType = ndf.FileType;
            if (fileType == EdataFileType.Unknown)
                fileType = EdataManager.GetFileTypeFromFileName(ndf.Name);

            return fileType == type;
        }

        protected bool HasSelectedFileOfType(EdataFileType type)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;
            if (vm == null || vm.SelectedFiles == null || vm.SelectedFiles.Count == 0)
                return false;

            return vm.SelectedFiles.Any(file =>
            {
                if (file == null)
                    return false;

                var fileType = file.FileType;
                if (fileType == EdataFileType.Unknown)
                    fileType = EdataManager.GetFileTypeFromFileName(file.Name);

                return fileType == type;
            });
        }

        protected bool HasEnding(string ending)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            var ndf = vm?.FilesCollectionView.CurrentItem as EdataContentFile;

            return ndf != null && ndf.Name.EndsWith(ending, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsImageFileForTextureExport(EdataContentFile file)
        {
            if (file == null)
                return false;

            var fileType = file.FileType;
            if (fileType == EdataFileType.Unknown)
                fileType = EdataManager.GetFileTypeFromFileName(file.Name);

            if (fileType == EdataFileType.Image)
                return true;

            return !string.IsNullOrWhiteSpace(file.Name) &&
                   file.Name.EndsWith(".tgv", StringComparison.OrdinalIgnoreCase);
        }

        protected void EditTradFileExecute(object obj)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            if (vm == null)
                return;

            var ndf = vm.FilesCollectionView.CurrentItem as EdataContentFile;

            if (ndf == null)
                return;

            var tradVm = new TradFileViewModel(ndf, vm);

            DialogProvider.ProvideView(tradVm, this);
        }

        protected void EditNdfbinExecute(object obj)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            if (vm == null)
                return;

            var ndf = vm.FilesCollectionView.CurrentItem as EdataContentFile;

            if (ndf == null)
                return;

            var dispatcher = Dispatcher.CurrentDispatcher;

            Action<ViewModelBase, ViewModelBase> open = DialogProvider.ProvideView;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
                {
                    try
                    {
                        dispatcher.Invoke(() => IsUIBusy = true);
                        dispatcher.Invoke(report, "Decompiling ndf binary...");

                        var detailsVm = new NdfEditorMainViewModel(ndf, vm);
                        dispatcher.Invoke(open, detailsVm, this);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                    }
                    finally
                    {
                        dispatcher.Invoke(() => IsUIBusy = false);
                        dispatcher.Invoke(report, "Ready");
                    }
                });

            s.Start();

        }

        protected void ExportNdfExecute(object obj)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            if (vm == null)
                return;

            var ndf = vm.FilesCollectionView.CurrentItem as EdataContentFile;

            if (ndf == null)
                return;

            Settings settings = SettingsManager.Load();

            byte[] content = new NdfbinReader().GetUncompressedNdfbinary(vm.EdataManager.GetRawData(ndf));

            var f = new FileInfo(ndf.Path);

            using (var fs = new FileStream(Path.Combine(settings.SavePath, f.Name), FileMode.OpenOrCreate))
            {
                fs.Write(content, 0, content.Length);
                fs.Flush();
            }
        }

        protected void ExportRawExecute(object obj)
        {
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            if (vm == null)
                return;

            var selectedFiles = vm.SelectedFiles
                .Where(file => file != null)
                .Distinct()
                .ToList();

            if (selectedFiles.Count == 0)
            {
                var currentFile = vm.FilesCollectionView.CurrentItem as EdataContentFile;
                if (currentFile != null)
                    selectedFiles.Add(currentFile);
            }

            if (selectedFiles.Count == 0)
                return;

            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);

                    Settings settings = SettingsManager.Load();
                    int exported = 0;
                    int failed = 0;

                    for (int i = 0; i < selectedFiles.Count; i++)
                    {
                        EdataContentFile file = selectedFiles[i];

                        try
                        {
                            var f = new FileInfo(file.Path);
                            string exportFullName = Path.Combine(settings.SavePath, settings.ExportWithFullPath ? file.Path : f.Name);
                            var exportDir = Path.GetDirectoryName(exportFullName);

                            if (!string.IsNullOrWhiteSpace(exportDir) && !Directory.Exists(exportDir))
                                Directory.CreateDirectory(exportDir);

                            dispatcher.Invoke(report, string.Format("Exporting {0} ({1}/{2})...", file.Path, i + 1, selectedFiles.Count));

                            byte[] buffer = vm.EdataManager.GetRawData(file);

                            using (var fs = new FileStream(exportFullName, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                fs.Write(buffer, 0, buffer.Length);
                                fs.Flush();
                            }

                            exported++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Trace.TraceError("Failed to export {0}: {1}", file.Path, ex);
                        }
                    }

                    dispatcher.Invoke(report, string.Format("Export complete. Exported: {0}, failed: {1}.", exported, failed));
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Unhandeled exception in Thread occoured: {0}", ex.ToString());
                    dispatcher.Invoke(report, string.Format("Export failed: {0}", ex.Message));
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        protected void ExportAll()
        {
            //foreach (var file in Files)
            //{
            //    var f = new FileInfo(file.Path);

            //    var dirToCreate = Path.Combine("c:\\temp\\", f.DirectoryName);

            //    if (!Directory.Exists(dirToCreate))
            //        Directory.CreateDirectory(dirToCreate);

            //    var buffer = NdfManager.GetRawData(file);
            //    using (var fs = new FileStream(Path.Combine(dirToCreate, f.Name), FileMode.OpenOrCreate))
            //    {
            //        fs.Write(buffer, 0, buffer.Length);
            //        fs.Flush();
            //    }

            //}
        }

        protected void ChangeExportPathExecute(object obj)
        {
            Settings settings = SettingsManager.Load();

            var folderDlg = new FolderBrowserDialog
            {
                SelectedPath = settings.SavePath,
                //RootFolder = Environment.SpecialFolder.MyComputer,
                ShowNewFolderButton = true,
            };

            if (folderDlg.ShowDialog() == DialogResult.OK)
            {
                settings.SavePath = folderDlg.SelectedPath;
                SettingsManager.Save(settings);
            }
        }

        protected void ChangeWargamePathExecute(object obj)
        {
            Settings settings = SettingsManager.Load();

            var folderDlg = new FolderBrowserDialog
            {
                SelectedPath = settings.WargamePath,
                //RootFolder = Environment.SpecialFolder.MyComputer,
                ShowNewFolderButton = true,
            };

            if (folderDlg.ShowDialog() == DialogResult.OK)
            {
                settings.WargamePath = folderDlg.SelectedPath;
                SettingsManager.Save(settings);

                bool keepZzFilter = Gamespace != null && Gamespace.ShowOnlyZzDatFiles;
                Gamespace = new GameSpaceViewModel(settings)
                {
                    ShowOnlyZzDatFiles = keepZzFilter
                };
                ZzDatSearchResults.Clear();
                ZzDatSearchQuery = string.Empty;
                OnPropertyChanged(() => Gamespace);

                AutoLoadWarnoPackages(settings.WargamePath);
            }
        }

        private void ChangePythonPathExecute(object obj)
        {
            Settings settings = SettingsManager.Load();

            var folderDlg = new FolderBrowserDialog
            {
                SelectedPath = settings.PythonPath,
                RootFolder = Environment.SpecialFolder.MyComputer,
                ShowNewFolderButton = true,
            };

            if (folderDlg.ShowDialog() == DialogResult.OK)
            {
                settings.PythonPath = folderDlg.SelectedPath;
                SettingsManager.Save(settings);
            }
        }

        protected void OpenFileExecute(object obj)
        {
            Settings settings = SettingsManager.Load();

            var openfDlg = new OpenFileDialog
            {
                DefaultExt = ".dat",
                Multiselect = true,
                Filter = "Edat (.dat)|*.dat|All Files|*.*"
            };

            if (File.Exists(settings.LastOpenFolder))
                openfDlg.InitialDirectory = settings.LastOpenFolder;


            if (openfDlg.ShowDialog().Value)
            {
                settings.LastOpenFolder = new FileInfo(openfDlg.FileName).DirectoryName;
                SettingsManager.Save(settings);
                foreach (string fileName in openfDlg.FileNames)
                {
                    HandleNewFile(fileName);
                }
            }
        }

        private void HandleNewFile(string fileName)
        {
            EdataFileType type;

            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var headerBuffer = new byte[12];
                fs.Read(headerBuffer, 0, headerBuffer.Length);

                type = EdataManager.GetFileTypeFromHeaderData(headerBuffer);

                if (type == EdataFileType.Unknown)
                    type = EdataManager.GetFileTypeFromFileName(fileName);

                if (type == EdataFileType.Ndfbin)
                {
                    var buffer = new byte[fs.Length];

                    fs.Seek(0, SeekOrigin.Begin);
                    fs.Read(buffer, 0, buffer.Length);

                    var detailsVm = new NdfEditorMainViewModel(buffer);

                    var view = new NdfbinView { DataContext = detailsVm };

                    view.Show();
                }
            }

            if (type == EdataFileType.Package)
                AddFile(fileName);
        }

        private static string BuildOpenFileSummary(EdataFileViewModel vm)
        {
            if (vm == null || vm.Files == null)
                return "Ready";

            var groupedByExtension = vm.Files
                .Select(f => Path.GetExtension(f.Name))
                .Select(ext => string.IsNullOrWhiteSpace(ext) ? "<noext>" : ext.ToLowerInvariant())
                .GroupBy(ext => ext)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => string.Format("{0}:{1}", g.Key, g.Count()));

            return string.Format(
                "Loaded {0} entries from {1}. Top extensions: {2}",
                vm.Files.Count,
                vm.HeaderText,
                string.Join(", ", groupedByExtension));
        }

        private void SearchInZzDatExecute(object obj)
        {
            string query = (ZzDatSearchQuery ?? string.Empty).Trim();
            if (query.Length < 2)
            {
                StatusText = "Enter at least 2 characters for ZZ DAT search.";
                return;
            }

            string rootPath = Gamespace != null ? Gamespace.RootPath : null;
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                StatusText = "Gamespace path does not exist. Please set your WARNO folder first.";
                return;
            }

            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var searchTask = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() =>
                    {
                        IsUIBusy = true;
                        ZzDatSearchResults.Clear();
                    });

                    List<string> zzFiles = FindAllNumberedZzDatFiles(rootPath)
                        .OrderBy(GetZzDatSortKey)
                        .ThenBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (zzFiles.Count == 0)
                    {
                        dispatcher.Invoke(report, string.Format("No ZZ_*.dat files found under {0}.", rootPath));
                        return;
                    }

                    int checkedFiles = 0;
                    int failed = 0;
                    var results = new List<string>();

                    foreach (string zzFile in zzFiles)
                    {
                        checkedFiles++;
                        dispatcher.Invoke(report, string.Format("Searching '{0}' in {1} ({2}/{3})...", query, Path.GetFileName(zzFile), checkedFiles, zzFiles.Count));

                        try
                        {
                            var manager = new EdataManager(zzFile);
                            manager.ParseEdataFile();

                            var matchingPaths = manager.Files
                                .Where(file => FileMatchesSearchQuery(file, query))
                                .Select(file => file.Path)
                                .Where(path => !string.IsNullOrWhiteSpace(path))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            if (matchingPaths.Count > 0)
                            {
                                string relativeDatPath = GetRelativePathSafe(rootPath, zzFile);
                                string firstMatch = matchingPaths[0];
                                results.Add(string.Format("{0} | matches: {1} | first: {2}", relativeDatPath, matchingPaths.Count, firstMatch));
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Trace.TraceError("Failed to search package {0}: {1}", zzFile, ex);
                        }
                    }

                    dispatcher.Invoke(() =>
                    {
                        foreach (string result in results)
                            ZzDatSearchResults.Add(result);
                    });

                    if (results.Count == 0)
                    {
                        dispatcher.Invoke(report, string.Format("No matches for '{0}' in {1} ZZ_*.dat files (failed: {2}).", query, zzFiles.Count, failed));
                    }
                    else
                    {
                        dispatcher.Invoke(report, string.Format(
                            "Search '{0}' complete. Found in {1}/{2} ZZ_*.dat files (failed: {3}).",
                            query,
                            results.Count,
                            zzFiles.Count,
                            failed));
                    }
                }
                catch (Exception ex)
                {
                    dispatcher.Invoke(report, string.Format("ZZ DAT search failed: {0}", ex.Message));
                    Trace.TraceError("ZZ DAT search failed under {0}: {1}", rootPath, ex);
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            searchTask.Start();
        }

        private void AutoLoadWarnoPackages(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return;

            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var scanTask = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, string.Format("Scanning {0} for ZZ*.dat packages...", rootPath));

                    List<string> zzFiles = FindZzDatFiles(rootPath)
                        .OrderBy(GetZzDatSortKey)
                        .ThenBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (zzFiles.Count == 0)
                    {
                        dispatcher.Invoke(report, string.Format("No ZZ*.dat files found under {0}.", rootPath));
                        return;
                    }

                    int opened = 0;
                    int skipped = 0;
                    int failed = 0;

                    foreach (string file in zzFiles)
                    {
                        bool alreadyOpen = dispatcher.Invoke(() => IsFileAlreadyOpen(file));
                        if (alreadyOpen)
                        {
                            skipped++;
                            continue;
                        }

                        dispatcher.Invoke(report, string.Format("Opening {0} ({1}/{2})...", Path.GetFileName(file), opened + skipped + failed + 1, zzFiles.Count));

                        try
                        {
                            dispatcher.Invoke(() => HandleNewFile(file));
                            opened++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Trace.TraceError("Failed to auto-open package {0}: {1}", file, ex);
                        }
                    }

                    dispatcher.Invoke(report, string.Format("WARNO scan complete. Found: {0}, opened: {1}, skipped: {2}, failed: {3}.", zzFiles.Count, opened, skipped, failed));
                }
                catch (Exception ex)
                {
                    dispatcher.Invoke(report, string.Format("Failed to scan WARNO directory: {0}", ex.Message));
                    Trace.TraceError("Failed to scan WARNO directory {0}: {1}", rootPath, ex);
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            scanTask.Start();
        }

        private bool IsFileAlreadyOpen(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return OpenFiles.Any(f => string.Equals(Path.GetFullPath(f.LoadedFile), fullPath, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> FindZzDatFiles(string rootPath)
        {
            List<string> directFiles = GetZzDatFilesInDirectory(rootPath);
            if (directFiles.Count > 0)
                return directFiles;

            var directoryToFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                IEnumerable<string> subDirectories = SafeEnumerateDirectories(current);

                foreach (string subDirectory in subDirectories)
                    pending.Push(subDirectory);

                List<string> zzFiles = GetZzDatFilesInDirectory(current);
                if (zzFiles.Count > 0)
                    directoryToFiles[current] = zzFiles;
            }

            if (directoryToFiles.Count == 0)
                return Enumerable.Empty<string>();

            string selectedDirectory = directoryToFiles.Keys
                .OrderByDescending(GetDirectoryVersionScore)
                .ThenByDescending(GetDirectoryLastWriteUtcSafe)
                .ThenByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .First();

            return directoryToFiles[selectedDirectory];
        }

        private static IEnumerable<string> FindAllNumberedZzDatFiles(string rootPath)
        {
            var matchingFiles = new List<string>();
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                foreach (string file in SafeEnumerateFiles(current, "ZZ*.dat"))
                {
                    if (IsNumberedZzDatFileName(Path.GetFileName(file)))
                        matchingFiles.Add(file);
                }

                IEnumerable<string> subDirectories = SafeEnumerateDirectories(current);
                foreach (string subDirectory in subDirectories)
                    pending.Push(subDirectory);
            }

            return matchingFiles;
        }

        private static bool FileMatchesSearchQuery(EdataContentFile file, string query)
        {
            if (file == null || string.IsNullOrWhiteSpace(query))
                return false;

            return (!string.IsNullOrWhiteSpace(file.Path) && file.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!string.IsNullOrWhiteSpace(file.Name) && file.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetRelativePathSafe(string rootPath, string fullPath)
        {
            try
            {
                return Path.GetRelativePath(rootPath, fullPath);
            }
            catch
            {
                return fullPath;
            }
        }

        private static bool IsNumberedZzDatFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            if (!fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                return false;

            string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            if (!withoutExtension.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = withoutExtension.Substring(3);
            return suffix.Length > 0 && suffix.All(char.IsDigit);
        }

        private static bool IsZzDatFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            if (!fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                return false;

            string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            if (string.Equals(withoutExtension, "ZZ", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!withoutExtension.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = withoutExtension.Substring(3);
            return suffix.Length > 0 && suffix.All(char.IsDigit);
        }

        private static int GetZzDatSortKey(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);

            if (string.Equals(name, "ZZ", StringComparison.OrdinalIgnoreCase))
                return -1;

            if (name.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = name.Substring(3);
                int index;
                if (int.TryParse(suffix, out index))
                    return index;
            }

            return int.MaxValue;
        }

        private static List<string> GetZzDatFilesInDirectory(string directory)
        {
            try
            {
                return Directory
                    .EnumerateFiles(directory, "ZZ*.dat", SearchOption.TopDirectoryOnly)
                    .Where(file => IsZzDatFileName(Path.GetFileName(file)))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string directory)
        {
            try
            {
                return Directory.EnumerateDirectories(directory);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static int GetDirectoryVersionScore(string directory)
        {
            int best = int.MinValue;
            string[] parts = directory.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                int value;
                if (int.TryParse(part, out value))
                    best = Math.Max(best, value);
            }

            return best;
        }

        private static DateTime GetDirectoryLastWriteUtcSafe(string directory)
        {
            try
            {
                return Directory.GetLastWriteTimeUtc(directory);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        protected void CloseFileExecute(object obj)
        {
            CloseFile(CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel);
        }

        protected void PlayMovieExecute(object obj)
        {
            const string name = "temp.wmv";
            var vm = CollectionViewSource.GetDefaultView(OpenFiles).CurrentItem as EdataFileViewModel;

            if (vm == null)
                return;

            var ndf = vm.FilesCollectionView.CurrentItem as EdataContentFile;

            if (ndf == null)
                return;

            Settings settings = SettingsManager.Load();

            byte[] buffer = vm.EdataManager.GetRawData(ndf);

            //var f = new FileInfo(ndf.Path);

            using (var fs = new FileStream(Path.Combine(settings.SavePath, name), FileMode.OpenOrCreate))
            {
                fs.Write(buffer, 0, buffer.Length);
                fs.Flush();
            }

            var detailsVm = new MoviePlaybackViewModel(Path.Combine(settings.SavePath, name));

            var view = new MoviePlaybackView { DataContext = detailsVm };

            view.Show();
        }

        protected void AboutUsExecute(object obj)
        {
            DialogProvider.ProvideView(new AboutViewModel(), this);
        }
    }
}
