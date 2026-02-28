using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using moddingSuite.BL.Ndf;
using moddingSuite.Model.Edata;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.Model.Ndfbin.Types.AllTypes;
using moddingSuite.View.DialogProvider;
using moddingSuite.ViewModel.Base;
using moddingSuite.ViewModel.Edata;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Microsoft.Win32;
using DialogResult = System.Windows.Forms.DialogResult;
using Form = System.Windows.Forms.Form;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using FormsButton = System.Windows.Forms.Button;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using FormsLabel = System.Windows.Forms.Label;
using FormsTextBox = System.Windows.Forms.TextBox;
using FormStartPosition = System.Windows.Forms.FormStartPosition;

namespace moddingSuite.ViewModel.Ndf
{
    public class NdfEditorMainViewModel : ViewModelBase
    {
        private readonly ObservableCollection<NdfClassViewModel> _classes = new ObservableCollection<NdfClassViewModel>();

        private ICollectionView _classesCollectionView;
        private string _classesFilterExpression = string.Empty;
        private string _statusText = string.Empty;

        private ICollectionView _stringCollectionView;
        private string _stringFilterExpression = string.Empty;
        private ObservableCollection<NdfStringReference> _strings;

        private ObservableCollection<NdfTranReference> _trans;
        private ICollectionView _transCollectionView;
        private string _transFilterExpression = string.Empty;
        private readonly NdfDecompressExportService _ndfDecompressExportService = new NdfDecompressExportService();
        private readonly NdfFieldByteMapService _ndfFieldByteMapService = new NdfFieldByteMapService();
        private readonly string _sourceNdfbinPath;

        public NdfEditorMainViewModel(EdataContentFile contentFile, EdataFileViewModel ownerVm)
        {
            OwnerFile = contentFile;
            EdataFileViewModel = ownerVm;
            _sourceNdfbinPath = null;

            var ndfbinReader = new NdfbinReader();
            NdfBinary = ndfbinReader.Read(ownerVm.EdataManager.GetRawData(contentFile));

            //var ndfbinManager = new NdfbinManager(ownerVm.EdataManager.GetRawData(contentFile));
            //NdfbinManager = ndfbinManager;

            //ndfbinManager.Initialize();

            InitializeNdfEditor();
        }

        /// <summary>
        /// Virtual call
        /// </summary>
        /// <param name="content"></param>
        public NdfEditorMainViewModel(byte[] content)
            : this(content, null)
        {
        }

        public NdfEditorMainViewModel(byte[] content, string sourceFilePath)
        {
            OwnerFile = null;
            EdataFileViewModel = null;
            _sourceNdfbinPath = sourceFilePath;

            var ndfbinReader = new NdfbinReader();
            NdfBinary = ndfbinReader.Read(content);

            InitializeNdfEditor();

            SaveNdfbinCommand = new ActionCommand(SaveNdfbinExecute, () => false);
        }

        public NdfEditorMainViewModel(NdfBinary ndf)
        {
            _sourceNdfbinPath = null;
            NdfBinary = ndf;

            InitializeNdfEditor();

            SaveNdfbinCommand = new ActionCommand(SaveNdfbinExecute, () => false);
        }

        private void InitializeNdfEditor()
        {
            foreach (NdfClass cls in NdfBinary.Classes)
                Classes.Add(new NdfClassViewModel(cls, this));

            Strings = NdfBinary.Strings;
            Trans = NdfBinary.Trans;

            SaveNdfbinCommand = new ActionCommand(SaveNdfbinExecute); //, () => NdfbinManager.ChangeManager.HasChanges);
            OpenInstanceCommand = new ActionCommand(OpenInstanceExecute);
            AddStringCommand = new ActionCommand(AddStringExecute);
            DeleteStringCommand = new ActionCommand(DeleteStringExecute);

            FindAllReferencesCommand = new ActionCommand(FindAllReferencesExecute);
            CopyInstanceCommand = new ActionCommand(CopyInstanceExecute);
            MakeTopObjectCommand = new ActionCommand(MakeTopObjectExecute);

            RunPythonScriptCommand = new ActionCommand(RunPythonScript);
            ExportDecompressedNdfCommand = new ActionCommand(ExportDecompressedNdfExecute, CanExportDecompressedNdfExecute);
            ExportDecompiledTextNdfCommand = new ActionCommand(ExportDecompiledTextNdfExecute, CanExportDecompressedNdfExecute);
            BatchDecompressNdfbinFolderCommand = new ActionCommand(BatchDecompressNdfbinFolderExecute);
            ExportByteMapFieldCommand = new ActionCommand(ExportByteMapFieldExecute, CanExportDecompressedNdfExecute);
            ExportByteMapAllCommand = new ActionCommand(ExportByteMapAllExecute, CanExportDecompressedNdfExecute);
        }

        private bool CanExportDecompressedNdfExecute()
        {
            return !string.IsNullOrWhiteSpace(_sourceNdfbinPath) &&
                   File.Exists(_sourceNdfbinPath) &&
                   string.Equals(Path.GetExtension(_sourceNdfbinPath), ".ndfbin", StringComparison.OrdinalIgnoreCase);
        }

        private void ExportDecompressedNdfExecute(object obj)
        {
            if (!CanExportDecompressedNdfExecute())
            {
                StatusText = "Decompression is available only for directly opened .ndfbin files.";
                return;
            }

            string sourcePath = _sourceNdfbinPath;

            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, string.Format("Decompressing {0}...", Path.GetFileName(sourcePath)));

                    NdfDecompressResult result = _ndfDecompressExportService.DecompressFileToSidecar(sourcePath);
                    if (result.Success)
                    {
                        dispatcher.Invoke(report, string.Format("Created {0} ({1} bytes).", result.OutputPath, result.OutputLength));
                    }
                    else
                    {
                        dispatcher.Invoke(report, string.Format("Decompression failed: {0}", result.ErrorMessage));
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Error while decompressing ndfbin file {0}: {1}", sourcePath, ex);
                    dispatcher.Invoke(report, string.Format("Decompression failed: {0}", ex.Message));
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        private void BatchDecompressNdfbinFolderExecute(object obj)
        {
            var folderDlg = new FolderBrowserDialog
            {
                ShowNewFolderButton = false
            };

            if (!string.IsNullOrWhiteSpace(_sourceNdfbinPath))
            {
                string sourceDirectory = Path.GetDirectoryName(_sourceNdfbinPath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
                    folderDlg.SelectedPath = sourceDirectory;
            }

            if (folderDlg.ShowDialog() != DialogResult.OK)
                return;

            string selectedFolder = folderDlg.SelectedPath;

            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, string.Format("Scanning {0} for .ndfbin files...", selectedFolder));

                    NdfDecompressBatchResult batchResult = _ndfDecompressExportService.DecompressFolder(selectedFolder, true);
                    if (batchResult.ProcessedCount == 0)
                    {
                        dispatcher.Invoke(report, string.Format("No .ndfbin files found under {0}.", selectedFolder));
                    }
                    else
                    {
                        dispatcher.Invoke(report, string.Format(
                            "Batch decompression complete. Processed: {0}, converted: {1}, failed: {2}.",
                            batchResult.ProcessedCount,
                            batchResult.ConvertedCount,
                            batchResult.FailedCount));
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Error while batch decompressing folder {0}: {1}", selectedFolder, ex);
                    dispatcher.Invoke(report, string.Format("Batch decompression failed: {0}", ex.Message));
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        private void ExportDecompiledTextNdfExecute(object obj)
        {
            if (!CanExportDecompressedNdfExecute())
            {
                StatusText = "Text decompilation is available only for directly opened .ndfbin files.";
                return;
            }

            string sourcePath = _sourceNdfbinPath;

            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, string.Format("Decompiling {0} to text (universal)...", Path.GetFileName(sourcePath)));

                    NdfDecompressResult result = _ndfDecompressExportService.DecompileFileToTextSidecarUniversal(sourcePath);
                    if (result.Success)
                    {
                        if (string.Equals(result.DecompileMode, "template-replay", StringComparison.OrdinalIgnoreCase))
                        {
                            dispatcher.Invoke(report, string.Format(
                                "Created original-like text NDF {0} ({1} bytes). Matched source: {2}.",
                                result.OutputPath,
                                result.OutputLength,
                                result.MatchedSourcePath));
                        }
                        else if (string.Equals(result.DecompileMode, "strict-divisions", StringComparison.OrdinalIgnoreCase))
                        {
                            dispatcher.Invoke(report, string.Format(
                                "Created strict text NDF {0} ({1} bytes). Source: {2}. Coverage: {3}/{4}.",
                                result.OutputPath,
                                result.OutputLength,
                                result.MatchedSourcePath,
                                result.MatchedDescriptors,
                                result.TotalDescriptors));
                        }
                        else
                        {
                            dispatcher.Invoke(report, string.Format(
                                "Created generic text NDF {0} ({1} bytes). Mode: {2}.",
                                result.OutputPath,
                                result.OutputLength,
                                result.DecompileMode));
                        }
                    }
                    else
                    {
                        dispatcher.Invoke(report, string.Format("Text decompilation failed: {0}", result.ErrorMessage));
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Error while decompiling ndfbin file {0} to text: {1}", sourcePath, ex);
                    dispatcher.Invoke(report, string.Format("Text decompilation failed: {0}", ex.Message));
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            s.Start();
        }

        private void ExportByteMapFieldExecute(object obj)
        {
            if (!CanExportDecompressedNdfExecute())
            {
                StatusText = "Byte-map is available only for directly opened .ndfbin files.";
                return;
            }

            string selector;
            bool accepted = PromptForText(
                "GUID.Property byte-map",
                "Enter selector (GUID.Property) or just Property name (GUID auto-detect):",
                string.Empty,
                out selector);

            if (!accepted)
                return;

            if (string.IsNullOrWhiteSpace(selector))
            {
                StatusText = "Selector is empty.";
                return;
            }

            string sourcePath = _sourceNdfbinPath;
            string outputDir = Path.GetDirectoryName(sourcePath);
            string selectedGuidHint = TryGetSelectedDescriptorGuidHint();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var task = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, string.Format("Building byte-map for {0}...", selector));

                    NdfFieldByteMapResult result = _ndfFieldByteMapService.MapField(sourcePath, selector, outputDir, selectedGuidHint);
                    if (!result.Success)
                    {
                        dispatcher.Invoke(report, string.Format("Byte-map failed: {0}", result.ErrorMessage));
                        return;
                    }

                    string firstOffsetText = result.FirstOffset.HasValue
                        ? string.Format("0x{0:X8}", result.FirstOffset.Value)
                        : "n/a";

                    dispatcher.Invoke(report, string.Format(
                        "Byte-map ready. Offsets: {0}, first: {1}. Report: {2}",
                        result.ChangedByteCount,
                        firstOffsetText,
                        result.ReportPath));
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Error while creating byte-map for selector {0}: {1}", selector, ex);
                    dispatcher.Invoke(report, string.Format("Byte-map failed: {0}", ex.Message));
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            task.Start();
        }

        private void ExportByteMapAllExecute(object obj)
        {
            if (!CanExportDecompressedNdfExecute())
            {
                StatusText = "Byte-map is available only for directly opened .ndfbin files.";
                return;
            }

            string maxCountInput;
            bool accepted = PromptForText(
                "Full byte-map",
                "Max selectors to process (empty = all):",
                "500",
                out maxCountInput);

            if (!accepted)
                return;

            int? maxCount = null;
            if (!string.IsNullOrWhiteSpace(maxCountInput))
            {
                int parsed;
                if (!int.TryParse(maxCountInput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
                {
                    StatusText = "Invalid max selector count.";
                    return;
                }

                maxCount = parsed;
            }

            string sourcePath = _sourceNdfbinPath;
            string outputDir = Path.GetDirectoryName(sourcePath);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var task = new Task(() =>
            {
                try
                {
                    dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, "Building full byte-map CSV...");

                    NdfFieldByteMapBulkResult result = _ndfFieldByteMapService.MapAll(sourcePath, outputDir, maxCount);
                    dispatcher.Invoke(report, string.Format(
                        "Full byte-map done. Processed: {0}, ok: {1}, failed: {2}. CSV: {3}",
                        result.ProcessedCount,
                        result.SucceededCount,
                        result.FailedCount,
                        result.CsvPath));
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Error while creating full byte-map: {0}", ex);
                    dispatcher.Invoke(report, string.Format("Full byte-map failed: {0}", ex.Message));
                }
                finally
                {
                    dispatcher.Invoke(() => IsUIBusy = false);
                }
            });

            task.Start();
        }

        private static bool PromptForText(string title, string labelText, string defaultValue, out string value)
        {
            value = null;

            using (var form = new Form())
            {
                form.Text = title;
                form.Width = 760;
                form.Height = 160;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;

                var label = new FormsLabel
                {
                    Left = 12,
                    Top = 12,
                    Width = 720,
                    Text = labelText
                };

                var textBox = new FormsTextBox
                {
                    Left = 12,
                    Top = 38,
                    Width = 720,
                    Text = defaultValue ?? string.Empty
                };

                var okButton = new FormsButton
                {
                    Text = "OK",
                    Left = 576,
                    Width = 75,
                    Top = 72,
                    DialogResult = FormsDialogResult.OK
                };

                var cancelButton = new FormsButton
                {
                    Text = "Cancel",
                    Left = 657,
                    Width = 75,
                    Top = 72,
                    DialogResult = FormsDialogResult.Cancel
                };

                form.Controls.Add(label);
                form.Controls.Add(textBox);
                form.Controls.Add(okButton);
                form.Controls.Add(cancelButton);
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;

                FormsDialogResult result = form.ShowDialog();
                if (result != FormsDialogResult.OK)
                    return false;

                value = textBox.Text;
                return true;
            }
        }

        private string TryGetSelectedDescriptorGuidHint()
        {
            try
            {
                var selectedClass = ClassesCollectionView == null ? null : ClassesCollectionView.CurrentItem as NdfClassViewModel;
                var selectedInstanceVm = selectedClass == null
                    ? null
                    : selectedClass.InstancesCollectionView == null
                        ? null
                        : selectedClass.InstancesCollectionView.CurrentItem as NdfObjectViewModel;

                if (selectedInstanceVm == null || selectedInstanceVm.Object == null)
                    return null;

                NdfObject instance = selectedInstanceVm.Object;
                NdfPropertyValue descriptorIdProp = instance.PropertyValues.FirstOrDefault(x =>
                    x.Property != null &&
                    string.Equals(x.Property.Name, "DescriptorId", StringComparison.Ordinal) &&
                    x.Type != NdfType.Unset &&
                    x.Value is NdfGuid);

                if (descriptorIdProp == null)
                    return null;

                var guidWrapper = descriptorIdProp.Value as NdfGuid;
                if (guidWrapper == null || guidWrapper.Value == null)
                    return null;

                Guid parsed;
                if (guidWrapper.Value is Guid)
                    parsed = (Guid)guidWrapper.Value;
                else if (!Guid.TryParse(guidWrapper.Value.ToString(), out parsed))
                    return null;

                return NdfScriptGuidNormalizer.NormalizeGuidForScript(parsed).ToUpperInvariant();
            }
            catch
            {
                return null;
            }
        }

        private void MakeTopObjectExecute(object obj)
        {
            var cls = ClassesCollectionView.CurrentItem as NdfClassViewModel;

            if (cls == null)
                return;

            var inst = cls.InstancesCollectionView.CurrentItem as NdfObjectViewModel;

            if (inst == null)
                return;

            if (!inst.Object.IsTopObject)
            {
                NdfBinary.TopObjects.Add(inst.Object.Id);
                inst.Object.IsTopObject = true;
            }
        }

        private void CopyInstanceExecute(object obj)
        {
            var cls = ClassesCollectionView.CurrentItem as NdfClassViewModel;

            if (cls == null)
                return;

            var inst = cls.InstancesCollectionView.CurrentItem as NdfObjectViewModel;

            if (inst == null)
                return;

            if (!inst.Object.IsTopObject)
            {
                MessageBox.Show("You can only create a copy of an top object.", "Information", MessageBoxButton.OK);
                return;
            }

            _copyInstanceResults = new List<NdfObject>();

            CopyInstance(inst.Object);

            var resultViewModel = new ObjectCopyResultViewModel(_copyInstanceResults, this);
            DialogProvider.ProvideView(resultViewModel, this);
        }

        private List<NdfObject> _copyInstanceResults;

        private NdfObject CopyInstance(NdfObject instToCopy)
        {
            NdfObject newInst = instToCopy.Class.Manager.CreateInstanceOf(instToCopy.Class, instToCopy.IsTopObject);

            _copyInstanceResults.Add(newInst);

            foreach (var propertyValue in instToCopy.PropertyValues)
            {
                if (propertyValue.Type == NdfType.Unset)
                    continue;

                var receiver = newInst.PropertyValues.Single(x => x.Property == propertyValue.Property);

                receiver.Value = GetCopiedValue(propertyValue);
            }

            instToCopy.Class.Instances.Add(newInst);

            var cls = Classes.SingleOrDefault(x => x.Object == instToCopy.Class);

            if (cls != null) cls.Instances.Add(new NdfObjectViewModel(newInst, cls.ParentVm));

            return newInst;
        }

        private NdfValueWrapper GetCopiedValue(IValueHolder toCopy)
        {
            NdfValueWrapper copiedValue = null;

            switch (toCopy.Value.Type)
            {
                case NdfType.ObjectReference:
                    var origInst = toCopy.Value as NdfObjectReference;

                    if (origInst != null && !origInst.Instance.IsTopObject)
                        copiedValue = new NdfObjectReference(origInst.Class, CopyInstance(origInst.Instance).Id);
                    else
                        copiedValue = NdfTypeManager.GetValue(toCopy.Value.GetBytes(), toCopy.Value.Type, toCopy.Manager);

                    break;
                case NdfType.List:
                    var copiedItems = new List<CollectionItemValueHolder>();
                    var collection = toCopy.Value as NdfCollection;
                    if (collection != null)
                    {
                        copiedItems.AddRange(collection.Select(entry => new CollectionItemValueHolder(GetCopiedValue(entry), toCopy.Manager)));
                    }

                    copiedValue = new NdfCollection(copiedItems);
                    break;
                case NdfType.MapList:
                    // creates Maplist type copy as NdfMaplist:Collection, written by Reros
                    var copiedMapItems = new NdfMapList();
                    var collectionMap = toCopy.Value as NdfCollection;
                    if (collectionMap != null)
                    {
                        foreach (var item in collectionMap)
                        {
                            copiedMapItems.Add(new CollectionItemValueHolder(GetCopiedValue(item), toCopy.Manager));
                        }
                    }
                    copiedValue = copiedMapItems;
                    break;

                case NdfType.Map:
                    var map = toCopy.Value as NdfMap;
                    if (map != null)
                        copiedValue = new NdfMap(new MapValueHolder(GetCopiedValue(map.Key), toCopy.Manager),
                            new MapValueHolder(GetCopiedValue(map.Value as IValueHolder), toCopy.Manager), toCopy.Manager);
                    break;

                default:
                    copiedValue = NdfTypeManager.GetValue(toCopy.Value.GetBytes(), toCopy.Value.Type, toCopy.Manager);
                    break;
            }

            return copiedValue;
        }

        private void FindAllReferencesExecute(object obj)
        {
            var cls = ClassesCollectionView.CurrentItem as NdfClassViewModel;

            if (cls == null)
                return;

            var inst = cls.InstancesCollectionView.CurrentItem as NdfObjectViewModel;

            if (inst == null)
                return;

            var result = new List<NdfPropertyValue>();

            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
            {
                try
                {
                    //dispatcher.Invoke(() => IsUIBusy = true);
                    dispatcher.Invoke(report, string.Format("Searching for references..."));

                    foreach (var instance in NdfBinary.Instances)
                        foreach (var propertyValue in instance.PropertyValues)
                            GetValue(propertyValue, inst, result, propertyValue);

                    var resultVm = new ReferenceSearchResultViewModel(result, this);

                    dispatcher.Invoke(() => DialogProvider.ProvideView(resultVm, this));
                    dispatcher.Invoke(report, string.Format("{0} references found", result.Count));
                }
                catch (Exception ex)
                {
                    Trace.TraceError(string.Format("Error while saving Ndfbin file: {0}", ex));
                    dispatcher.Invoke(report, "Error while searching");
                }
            });

            s.Start();
        }

        private void GetValue(IValueHolder valueHolder, NdfObjectViewModel inst, List<NdfPropertyValue> result, NdfPropertyValue propertyValue)
        {
            switch (valueHolder.Value.Type)
            {
                case NdfType.ObjectReference:
                    var ndfObjectReference = valueHolder.Value as NdfObjectReference;
                    if (ndfObjectReference != null && ndfObjectReference.InstanceId == inst.Object.Id)
                        result.Add(propertyValue);
                    break;
                case NdfType.List:
                case NdfType.MapList:
                    var ndfCollection = valueHolder.Value as NdfCollection;
                    if (ndfCollection != null)
                        foreach (var col in ndfCollection)
                            GetValue(col, inst, result, propertyValue);
                    break;
                case NdfType.Map:
                    var map = valueHolder.Value as NdfMap;
                    if (map != null)
                    {
                        GetValue(map.Key, inst, result, propertyValue);
                        GetValue(map.Value as IValueHolder, inst, result, propertyValue);
                    }
                    break;
            }
        }

        private void RunPythonScript(object _)
        {
            var scriptDlg = new OpenFileDialog
            {
                DefaultExt = ".py",
                Filter = "Python script (.py)|*.py|All Files|*.*"
            };

            if (scriptDlg.ShowDialog().Value)
            {
                var engine = Python.CreateEngine();
                engine.Runtime.LoadAssembly(Assembly.GetExecutingAssembly());
                var scope = engine.CreateScope();
                scope.SetVariable("NdfBinary", NdfBinary);
                scope.SetVariable("Classes", new NdfScriptableClassList(Classes));
                try
                {
                    engine.ExecuteFile(scriptDlg.FileName, scope);
                }
                catch (Exception e)
                {
                    var exceptionOps = engine.GetService<ExceptionOperations>();
                    MessageBox.Show(exceptionOps.FormatException(e));
                    return;
                }
                MessageBox.Show("Script successfully executed!");
            }
        }

        public NdfBinary NdfBinary { get; protected set; }

        protected EdataFileViewModel EdataFileViewModel { get; set; }
        protected EdataContentFile OwnerFile { get; set; }

        public ICommand SaveNdfbinCommand { get; set; }
        public ICommand OpenInstanceCommand { get; set; }
        public ICommand AddStringCommand { get; set; }
        public ICommand DeleteStringCommand { get; set; }
        public ICommand FindAllReferencesCommand { get; set; }
        public ICommand CopyInstanceCommand { get; set; }
        public ICommand MakeTopObjectCommand { get; set; }
        public ICommand RunPythonScriptCommand { get; set; }
        public ICommand ExportDecompressedNdfCommand { get; set; }
        public ICommand ExportDecompiledTextNdfCommand { get; set; }
        public ICommand BatchDecompressNdfbinFolderCommand { get; set; }
        public ICommand ExportByteMapFieldCommand { get; set; }
        public ICommand ExportByteMapAllCommand { get; set; }

        public string Title
        {
            get
            {
                string path = "Virtual";

                if (OwnerFile != null)
                    path = OwnerFile.Path;

                return string.Format("Ndf Editor [{0}]", path);
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            set
            {
                _statusText = value;
                OnPropertyChanged(() => StatusText);
            }
        }

        public string ClassesFilterExpression
        {
            get { return _classesFilterExpression; }
            set
            {
                _classesFilterExpression = value;
                OnPropertyChanged(() => ClassesFilterExpression);

                ClassesCollectionView.Refresh();
            }
        }

        public string StringFilterExpression
        {
            get { return _stringFilterExpression; }
            set
            {
                _stringFilterExpression = value;
                OnPropertyChanged(() => StringFilterExpression);
                StringCollectionView.Refresh();
            }
        }

        public string TransFilterExpression
        {
            get { return _transFilterExpression; }
            set
            {
                _transFilterExpression = value;
                OnPropertyChanged(() => TransFilterExpression);
                TransCollectionView.Refresh();
            }
        }

        public ICollectionView ClassesCollectionView
        {
            get
            {
                if (_classesCollectionView == null)
                {
                    BuildClassesCollectionView();
                }
                return _classesCollectionView;
            }
        }

        public ICollectionView StringCollectionView
        {
            get
            {
                if (_stringCollectionView == null)
                {
                    BuildStringCollectionView();
                }

                return _stringCollectionView;
            }
        }

        public ICollectionView TransCollectionView
        {
            get
            {
                if (_transCollectionView == null)
                {
                    BuildTransCollectionView();
                }

                return _transCollectionView;
            }
        }

        public ObservableCollection<NdfClassViewModel> Classes
        {
            get { return _classes; }
        }

        public ObservableCollection<NdfStringReference> Strings
        {
            get { return _strings; }
            set
            {
                _strings = value;
                OnPropertyChanged(() => Strings);
            }
        }

        public ObservableCollection<NdfTranReference> Trans
        {
            get { return _trans; }
            set
            {
                _trans = value;
                OnPropertyChanged(() => Trans);
            }
        }

        private void BuildClassesCollectionView()
        {
            _classesCollectionView = CollectionViewSource.GetDefaultView(Classes);
            _classesCollectionView.Filter = FilterClasses;

            OnPropertyChanged(() => ClassesCollectionView);
        }

        private void BuildStringCollectionView()
        {
            _stringCollectionView = CollectionViewSource.GetDefaultView(Strings);
            _stringCollectionView.Filter = FilterStrings;

            OnPropertyChanged(() => StringCollectionView);
        }

        private void BuildTransCollectionView()
        {
            _transCollectionView = CollectionViewSource.GetDefaultView(Trans);
            _transCollectionView.Filter = FilterTrans;

            OnPropertyChanged(() => TransCollectionView);
        }

        public bool FilterClasses(object o)
        {
            var clas = o as NdfClassViewModel;

            if (clas == null || ClassesFilterExpression == string.Empty)
                return true;

            string[] parts = ClassesFilterExpression.Split(new[] { ":" }, StringSplitOptions.RemoveEmptyEntries);

            int cls;
            
            if (parts.Length > 1 && Int32.TryParse(parts[0], out cls) && (clas.Id == cls || clas.Name == parts[0]))
            {
                // filters like this 26:2556 with Classnumber:Instancenumber, could be removed?
                int inst;
                if (Int32.TryParse(parts[1], out inst))
                {
                    NdfObjectViewModel instObj = clas.Instances.SingleOrDefault(x => x.Id == inst);

                    if (instObj != null)
                        clas.InstancesCollectionView.MoveCurrentTo(instObj);
                }
            }
            else
            {
                int inst;
                if (Int32.TryParse(parts[0], out inst))
                {
                    // selects instance searched for added by Reros
                    NdfObjectViewModel instObj = clas.Instances.SingleOrDefault(x => x.Id == inst);

                    if (instObj != null)
                        clas.InstancesCollectionView.MoveCurrentTo(instObj);
                }
            }

            return clas.Name.ToLower().Contains(parts[0].ToLower()) ||
                   clas.Id.ToString(CultureInfo.CurrentCulture).Contains(parts[0]) ||
                   clas.Instances.Any(x => x.Id.ToString(CultureInfo.InvariantCulture) == parts[0]);
        }

        public bool FilterStrings(object o)
        {
            var str = o as NdfStringReference;

            if (str == null || StringFilterExpression == string.Empty)
                return true;

            return str.Value.ToLower().Contains(StringFilterExpression.ToLower()) ||
                   str.Id.ToString(CultureInfo.CurrentCulture).Contains(StringFilterExpression);
        }

        public bool FilterTrans(object o)
        {
            var tran = o as NdfTranReference;

            if (tran == null || TransFilterExpression == string.Empty)
                return true;

            return tran.Value.ToLower().Contains(TransFilterExpression.ToLower()) ||
                   tran.Id.ToString(CultureInfo.CurrentCulture).Contains(TransFilterExpression);
        }

        private void SaveNdfbinExecute(object obj)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Action<string> report = msg => StatusText = msg;

            var s = new Task(() =>
                {
                    try
                    {
                        dispatcher.Invoke(() => IsUIBusy = true);
                        dispatcher.Invoke(report, string.Format("Saving back changes..."));

                        var writer = new NdfbinWriter();
                        byte[] newFile = writer.Write(NdfBinary, NdfBinary.Header.IsCompressedBody);
                        dispatcher.Invoke(report, string.Format("Recompiling of {0} finished! ", EdataFileViewModel.EdataManager.FilePath));

                        EdataFileViewModel.EdataManager.ReplaceFile(OwnerFile, newFile);
                        dispatcher.Invoke(report, "Replacing new File in edata finished!");

                        EdataFileViewModel.LoadFile(EdataFileViewModel.LoadedFile);

                        EdataContentFile newOwen = EdataFileViewModel.EdataManager.Files.Single(x => x.Path == OwnerFile.Path);

                        OwnerFile = newOwen;
                        dispatcher.Invoke(report, string.Format("Saving of changes finished! {0}", EdataFileViewModel.EdataManager.FilePath));
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError(string.Format("Error while saving Ndfbin file: {0}", ex));
                        dispatcher.Invoke(report, "Saving interrupted - Did you start Wargame before I was ready?");
                    }
                    finally
                    {
                        dispatcher.Invoke(() => IsUIBusy = false);
                    }
                });
            s.Start();
        }

        private void OpenInstanceExecute(object obj)
        {
            var cls = obj as NdfObjectViewModel;

            if (cls == null)
                return;
            var vm = new NdfClassViewModel(cls.Object.Class, this);
            NdfObjectViewModel inst = vm.Instances.SingleOrDefault(x => x.Id == cls.Id);
            ViewModelBase baseViewModel;
            switch (cls.Object.Class.Name)
            {
                case "TGameplayArmeArmureContainer":
                case "TGameplayDamageResistanceContainer":
                    baseViewModel = new ArmourDamageViewModel(inst.Object, this);
                    break;
                default:
                    if (inst == null)
                        return;
                    vm.InstancesCollectionView.MoveCurrentTo(inst);
                    baseViewModel = vm;
                    break;
            }
            DialogProvider.ProvideView(baseViewModel, this);
        }

        private void DeleteStringExecute(object obj)
        {
            var cur = StringCollectionView.CurrentItem as NdfStringReference;

            if (cur == null)
                return;

            Strings.Remove(cur);
        }

        private void AddStringExecute(object obj)
        {
            Strings.Add(new NdfStringReference { Id = Strings.Count, Value = "<New string>" });
            StringCollectionView.MoveCurrentToLast();
        }
    }
}
