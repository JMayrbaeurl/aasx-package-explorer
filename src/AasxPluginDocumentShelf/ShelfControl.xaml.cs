﻿/*
Copyright (c) 2018-2021 Festo AG & Co. KG <https://www.festo.com/net/de_de/Forms/web/contact_international>
Author: Michael Hoffmeister

This source code is licensed under the Apache License 2.0 (see LICENSE.txt).

This source code may use other Open Source software components (see LICENSE.txt).
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using AasxIntegrationBase;
using AasxIntegrationBase.AasForms;
using AasxPredefinedConcepts;
using AdminShellNS;
using Newtonsoft.Json;

// ReSharper disable InconsistentlySynchronizedField
// checks and everything looks fine .. maybe .Count() is already treated as synchronized action?

namespace AasxPluginDocumentShelf
{
    public partial class ShelfControl : UserControl
    {
        #region Members
        //=============

        private LogInstance Log = new LogInstance();
        private AdminShellPackageEnv thePackage = null;
        private AdminShell.Submodel theSubmodel = null;
        private DocumentShelfOptions theOptions = null;
        private static DocuShelfSemanticConfig _semConfig = DocuShelfSemanticConfig.CreateDefault();
        private PluginEventStack theEventStack = null;

        private string convertableFiles = ".pdf .jpeg .jpg .png .bmp .pdf .xml .txt *";

        private List<DocumentEntity> theDocEntitiesToPreview = new List<DocumentEntity>();

        #endregion

        #region View Model
        //================

        private ViewModel theViewModel = new ViewModel();

        public class ViewModel : AasxIntegrationBase.WpfViewModelBase
        {

            private int theSelectedDocClass = 0;
            public int TheSelectedDocClass
            {
                get { return theSelectedDocClass; }
                set
                {
                    theSelectedDocClass = value;
                    RaisePropertyChanged("TheSelectedDocClass");
                    RaiseViewModelChanged();
                }
            }

            private AasxLanguageHelper.LangEnum theSelectedLanguage = AasxLanguageHelper.LangEnum.Any;
            public AasxLanguageHelper.LangEnum TheSelectedLanguage
            {
                get { return theSelectedLanguage; }
                set
                {
                    theSelectedLanguage = value;
                    RaisePropertyChanged("TheSelectedLanguage");
                    RaiseViewModelChanged();
                }
            }

            public enum ListType { Bars, Grid };
            private ListType theSelectedListType = ListType.Bars;
            public ListType TheSelectedListType
            {
                get { return theSelectedListType; }
                set
                {
                    theSelectedListType = value;
                    RaisePropertyChanged("TheSelectedListType");
                    RaiseViewModelChanged();
                }
            }
        }

        #endregion
        #region Cache for already generated Images
        //========================================

        private static Dictionary<string, BitmapImage> referableHashToCachedBitmap =
            new Dictionary<string, BitmapImage>();

        #endregion
        #region Init of component
        //=======================

        public void ResetCountryRadioButton(RadioButton radio, CountryFlag.CountryCode code)
        {
            if (radio != null && radio.Content != null && radio.Content is WrapPanel wrap)
            {
                wrap.Children.Clear();
                var cf = new CountryFlag.CountryFlag();
                cf.Code = code;
                cf.Width = 30;
                wrap.Children.Add(cf);
            }
        }

        public ShelfControl()
        {
            InitializeComponent();

            // combo box needs init
            ComboClassId.Items.Clear();
            foreach (var dc in (DefinitionsVDI2770.Vdi2770DocClass[])Enum.GetValues(
                                                                         typeof(DefinitionsVDI2770.Vdi2770DocClass)))
                ComboClassId.Items.Add(
                    "" + DefinitionsVDI2770.GetDocClass(dc) + " - " + DefinitionsVDI2770.GetDocClassName(dc));

            ComboClassId.SelectedIndex = 0;

            // bind to view model
            this.DataContext = this.theViewModel;
            this.theViewModel.ViewModelChanged += TheViewModel_ViewModelChanged;

            var entities = new List<DocumentEntity>();
            entities.Add(new DocumentEntity("Titel", "Orga", "cdskcnsdkjcnkjsckjsdjn", new[] { "de", "GB" }));
            ScrollMainContent.ItemsSource = entities;

            // a bit hacky: explicetly load CountryFlag.dll
#if __not_working__in_Release__
            var x = CountryFlag.CountryCode.DE;
            if (x != CountryFlag.CountryCode.DE)
            {
                return null;
            }
#else
            // CountryFlag does not work in XAML (at least not in Release binary)
            ResetCountryRadioButton(RadioLangEN, CountryFlag.CountryCode.GB);
            ResetCountryRadioButton(RadioLangDE, CountryFlag.CountryCode.DE);
            ResetCountryRadioButton(RadioLangCN, CountryFlag.CountryCode.CN);
            ResetCountryRadioButton(RadioLangJP, CountryFlag.CountryCode.JP);
            ResetCountryRadioButton(RadioLangKR, CountryFlag.CountryCode.KR);
            ResetCountryRadioButton(RadioLangFR, CountryFlag.CountryCode.FR);
            ResetCountryRadioButton(RadioLangES, CountryFlag.CountryCode.ES);
#endif

            // Timer for loading
            System.Windows.Threading.DispatcherTimer dispatcherTimer = new System.Windows.Threading.DispatcherTimer();
            dispatcherTimer.Tick += DispatcherTimer_Tick;
            dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, 1000);
            dispatcherTimer.Start();
        }

        private object mutexDocEntitiesInPreview = new object();
        private int numDocEntitiesInPreview = 0;
        private const int maxDocEntitiesInPreview = 3;

        private void DispatcherTimer_Tick(object sender, EventArgs e)
        {
            // each tick check for one image, if a preview shall be done
            if (theDocEntitiesToPreview != null && theDocEntitiesToPreview.Count > 0 &&
                numDocEntitiesInPreview < maxDocEntitiesInPreview)
            {
                // pop
                DocumentEntity ent = null;
                lock (theDocEntitiesToPreview)
                {
                    ent = theDocEntitiesToPreview[0];
                    theDocEntitiesToPreview.RemoveAt(0);
                }

                try
                {
                    // temp input
                    var inputFn = ent?.DigitalFile?.Path;
                    if (inputFn != null)
                    {

                        // from package?
                        if (CheckIfPackageFile(inputFn))
                            inputFn = thePackage.MakePackageFileAvailableAsTempFile(ent.DigitalFile.Path);

                        // temp output
                        string outputFn = System.IO.Path.GetTempFileName().Replace(".tmp", ".png");

                        // remember these for later deletion
                        ent.DeleteFilesAfterLoading = new[] { inputFn, outputFn };

                        // start process
                        string arguments = string.Format("-flatten -density 75 \"{0}\"[0] \"{1}\"", inputFn, outputFn);
                        string exeFn = System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "convert.exe");

                        var startInfo = new ProcessStartInfo(exeFn, arguments)
                        {
                            WindowStyle = ProcessWindowStyle.Hidden
                        };

                        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

                        DocumentEntity lambdaEntity = ent;
                        string outputFnBuffer = outputFn;
                        process.Exited += (sender2, args) =>
                        {
                            // release number of parallel processes
                            lock (mutexDocEntitiesInPreview)
                            {
                                numDocEntitiesInPreview--;
                            }

                            // take over data?
                            if (lambdaEntity.ImgContainer != null)
                            {
                                // trigger display image
                                lambdaEntity.ImageReadyToBeLoaded = outputFnBuffer;
                            }
                        };

                        try
                        {
                            process.Start();
                        }
                        catch (Exception ex)
                        {
                            AdminShellNS.LogInternally.That.Error(
                                ex, $"Failed to start the process: {startInfo.FileName} " +
                                    $"with arguments {string.Join(" ", startInfo.Arguments)}");
                        }

                        // limit the number of parallel executions
                        lock (mutexDocEntitiesInPreview)
                        {
                            numDocEntitiesInPreview++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AdminShellNS.LogInternally.That.SilentlyIgnoredError(ex);
                }
            }

            // over all items in order to check, if a prepared image shall be displayed
            foreach (var x in this.ScrollMainContent.Items)
            {
                var de = x as DocumentEntity;
                if (de == null)
                    continue;

                if (de.ImageReadyToBeLoaded != null)
                {
                    // never again
                    var tempFn = de.ImageReadyToBeLoaded;
                    de.ImageReadyToBeLoaded = null;

                    // try load
                    try
                    {
                        // convert here, as the tick-Thread in STA / UI thread
                        var bi = de.LoadImageFromPath(tempFn);

                        // now delete the associated files file!
                        if (de.DeleteFilesAfterLoading != null)
                            foreach (var fn in de.DeleteFilesAfterLoading)
                                // it is quite likely (e.g. http:// files) that the delete fails!
                                try
                                {
                                    File.Delete(fn);
                                }
                                catch (Exception ex)
                                {
                                    AdminShellNS.LogInternally.That.SilentlyIgnoredError(ex);
                                }

                        // remember in the cache
                        if (bi != null
                            && referableHashToCachedBitmap != null
                            && !referableHashToCachedBitmap.ContainsKey(de.ReferableHash))
                            referableHashToCachedBitmap[de.ReferableHash] = bi;
                    }
                    catch (Exception ex)
                    {
                        AdminShellNS.LogInternally.That.SilentlyIgnoredError(ex);
                    }
                }
            }
        }

        public void Start(
            LogInstance log,
            AdminShellPackageEnv thePackage,
            AdminShell.Submodel theSubmodel,
            AasxPluginDocumentShelf.DocumentShelfOptions theOptions,
            PluginEventStack eventStack)
        {
            this.Log = log;
            this.thePackage = thePackage;
            this.theSubmodel = theSubmodel;
            this.theOptions = theOptions;
            this.theEventStack = eventStack;
        }

        public static ShelfControl FillWithWpfControls(
            LogInstance log,
            object opackage, object osm,
            AasxPluginDocumentShelf.DocumentShelfOptions options,
            PluginEventStack eventStack,
            object masterDockPanel)
        {
            // access
            var package = opackage as AdminShellPackageEnv;
            var sm = osm as AdminShell.Submodel;
            var master = masterDockPanel as DockPanel;
            if (package == null || sm == null || master == null)
            {
                return null;
            }

            // the Submodel elements need to have parents
            sm.SetAllParents();

            // create TOP control
            var shelfCntl = new ShelfControl();
            shelfCntl.Start(log, package, sm, options, eventStack);
            master.Children.Add(shelfCntl);

            // return shelf
            return shelfCntl;
        }

        public void HandleEventReturn(AasxPluginEventReturnBase evtReturn)
        {
            if (this.currentFormInst?.subscribeForNextEventReturn != null)
            {
                // delete first
                var tempLambda = this.currentFormInst.subscribeForNextEventReturn;
                this.currentFormInst.subscribeForNextEventReturn = null;

                // execute
                tempLambda(evtReturn);
            }
        }

        private void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            // user control was loaded, all options shall be set and outer grid is loaded fully ..
            ParseSubmodelToListItems(
                this.theSubmodel, this.theOptions, theViewModel.TheSelectedDocClass,
                theViewModel.TheSelectedLanguage, theViewModel.TheSelectedListType);
        }


        #endregion

        #region REDRAW of contents

        private void TheViewModel_ViewModelChanged(AasxIntegrationBase.WpfViewModelBase obj)
        {
            // re-display
            ParseSubmodelToListItems(
                this.theSubmodel, this.theOptions, theViewModel.TheSelectedDocClass,
                theViewModel.TheSelectedLanguage, theViewModel.TheSelectedListType);
        }

        private bool CheckIfPackageFile(string fn)
        {
            return fn.StartsWith(@"/");
        }

        private bool CheckIfConvertableFile(string fn)
        {
            var ext = System.IO.Path.GetExtension(fn.ToLower());
            if (ext == "")
                ext = "*";

            // check
            return (convertableFiles.Contains(ext));
        }

        private void ParseSubmodelToListItems(
            AdminShell.Submodel subModel, AasxPluginDocumentShelf.DocumentShelfOptions options,
            int selectedDocClass, AasxLanguageHelper.LangEnum selectedLanguage, ViewModel.ListType selectedListType)
        {
            try
            {
                // influence list view rendering, as well
                if (selectedListType == ViewModel.ListType.Bars)
                {
                    ScrollMainContent.ItemTemplate = (DataTemplate)ScrollMainContent.Resources["ItemTemplateForBar"];
                    ScrollMainContent.ItemsPanel = (ItemsPanelTemplate)ScrollMainContent.Resources["ItemsPanelForBar"];
                }

                if (selectedListType == ViewModel.ListType.Grid)
                {
                    ScrollMainContent.ItemTemplate = (DataTemplate)ScrollMainContent.Resources["ItemTemplateForGrid"];
                    ScrollMainContent.ItemsPanel =
                        (ItemsPanelTemplate)ScrollMainContent.Resources["ItemsPanelForGrid"];
                }

                // clean table
                ScrollMainContent.ItemsSource = null;

                // access
                if (subModel?.semanticId == null || options == null)
                    return;

                // make sure for the right Submodel
                DocumentShelfOptionsRecord foundRec = null;
                foreach (var rec in options.LookupAllIndexKey<DocumentShelfOptionsRecord>(
                    subModel?.semanticId?.GetAsExactlyOneKey()))
                    foundRec = rec;

                if (foundRec == null)
                    return;

                // right now: hardcoded check for mdoel version
                var modelVersion = DocumentEntity.SubmodelVersion.Default;
                var defs11 = AasxPredefinedConcepts.VDI2770v11.Static;
                if (subModel.semanticId.Matches(defs11?.SM_ManufacturerDocumentation?.GetSemanticKey()))
                    modelVersion = DocumentEntity.SubmodelVersion.V11;
                if (foundRec.ForceVersion == DocumentEntity.SubmodelVersion.V10)
                    modelVersion = DocumentEntity.SubmodelVersion.V10;
                if (foundRec.ForceVersion == DocumentEntity.SubmodelVersion.V11)
                    modelVersion = DocumentEntity.SubmodelVersion.V11;

                // set checkbox
                this.CheckBoxLatestVersion.IsChecked = modelVersion == DocumentEntity.SubmodelVersion.V11;

                // set usage info
                var useinf = foundRec.UsageInfo;
                TextBlockUsageInfo.Text = useinf;
                PanelUsageInfo.Visibility = useinf.HasContent() ? Visibility.Visible : Visibility.Collapsed;

                // what defaultLanguage
                string defaultLang = null;
                if (theViewModel != null && theViewModel.TheSelectedLanguage > AasxLanguageHelper.LangEnum.Any)
                    defaultLang = AasxLanguageHelper.LangEnumToISO639String[(int)theViewModel.TheSelectedLanguage];

                // make new list box items
                var its = new List<DocumentEntity>();
                if (modelVersion != DocumentEntity.SubmodelVersion.V11)
                    its = ListOfDocumentEntity.ParseSubmodelForV10(
                        thePackage, subModel, options, defaultLang, selectedDocClass, selectedLanguage);
                else
                    its = ListOfDocumentEntity.ParseSubmodelForV11(
                        thePackage, subModel, defs11, defaultLang, selectedDocClass, selectedLanguage);

                // post process
                foreach (var ent in its)
                {
                    // make viewbox to host __later__ created image!
                    var vb = new Viewbox();
                    vb.Stretch = Stretch.Uniform;
                    ent.ImgContainer = vb;

                    // if a preview file exists, try load directly, but not interfere
                    // we delayed load logic, as these images might get more detailed
                    if (ent.PreviewFile?.Path?.HasContent() == true)
                    {
                        var inputFn = ent.PreviewFile.Path;

                        // from package?
                        if (CheckIfPackageFile(inputFn))
                            inputFn = thePackage.MakePackageFileAvailableAsTempFile(ent.PreviewFile.Path);

                        ent.LoadImageFromPath(inputFn);
                    }

                    // delayed load logic
                    // can already put a generated image into the viewbox?
                    if (referableHashToCachedBitmap != null &&
                        referableHashToCachedBitmap.ContainsKey(ent.ReferableHash))
                    {
                        var img = new Image();
                        img.Source = referableHashToCachedBitmap[ent.ReferableHash];
                        ent.ImgContainer.Child = img;
                    }
                    else
                    {
                        // trigger generation of image

                        // check if already in list
                        DocumentEntity foundDe = null;
                        foreach (var de in theDocEntitiesToPreview)
                            if (ent.ReferableHash == de.ReferableHash)
                                foundDe = de;

                        lock (theDocEntitiesToPreview)
                        {
                            if (foundDe != null)
                                theDocEntitiesToPreview.Remove(foundDe);
                            theDocEntitiesToPreview.Add(ent);
                        }
                    }

                    // attach events and add
                    ent.DoubleClick += DocumentEntity_DoubleClick;
                    ent.MenuClick += DocumentEntity_MenuClick;
                    ent.DragStart += DocumentEntity_DragStart;
                }

                // finally set
                ScrollMainContent.ItemsSource = its;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception when parse/ display Submodel");
            }
        }

        private void DocumentEntity_MenuClick(DocumentEntity e, string menuItemHeader, object tag)
        {
            // first check
            if (e == null || menuItemHeader == null)
                return;

            // what to do?
            if (tag == null && menuItemHeader == "Edit" && e.SourceElementsDocument != null &&
                e.SourceElementsDocumentVersion != null)
            {
                // show the edit panel
                OuterTabControl.SelectedItem = TabPanelEdit;
                ButtonAddUpdateDoc.Content = "Update";

                // make a template description for the content (remeber it)
                formInUpdateMode = true;
                updateSourceElements = e.SourceElementsDocument;

                var desc = DocuShelfSemanticConfig.CreateVdi2770TemplateDesc(theOptions);

                // latest version (from resources)
                if (e.SmVersion == DocumentEntity.SubmodelVersion.V11)
                {
                    desc = DocuShelfSemanticConfig.CreateVdi2770v11TemplateDesc();
                }

                this.currentFormDescription = desc;

                // take over existing data
                this.currentFormInst = new FormInstanceSubmodelElementCollection(null, currentFormDescription);
                this.currentFormInst.PresetInstancesBasedOnSource(updateSourceElements);
                this.currentFormInst.outerEventStack = theEventStack;

                // bring it to the panel
                var elementsCntl = new FormListOfDifferentControl();
                elementsCntl.ShowHeader = false;
                elementsCntl.DataContext = this.currentFormInst;
                ScrollViewerForm.Content = elementsCntl;

                // OK
                return;
            }

            if (tag == null && menuItemHeader == "Delete" && e.SourceElementsDocument != null &&
                e.SourceElementsDocumentVersion != null && theSubmodel?.submodelElements != null && theOptions != null)
            {
                // the source elements need to match a Document
                foreach (var smcDoc in
                    theSubmodel.submodelElements.FindAllSemanticIdAs<AdminShell.SubmodelElementCollection>(
                        _semConfig.SemIdDocument))
                    if (smcDoc?.value == e.SourceElementsDocument)
                    {
                        // identify as well the DocumentVersion
                        // (convert to List() because of Count() below)
                        var allVers =
                            e.SourceElementsDocument.FindAllSemanticIdAs<AdminShell.SubmodelElementCollection>(
                                _semConfig.SemIdDocumentVersion).ToList();
                        foreach (var smcVer in allVers)
                            if (smcVer?.value == e.SourceElementsDocumentVersion)
                            {
                                // access
                                if (smcVer == null || smcVer.value == null || smcDoc == null || smcDoc.value == null)
                                    continue;

                                // ask back .. the old-fashioned way!
                                if (MessageBoxResult.Yes != MessageBox.Show(
                                    "Delete Document?", "Question", MessageBoxButton.YesNo, MessageBoxImage.Warning))
                                    return;

                                // confirmed! -> delete
                                if (allVers.Count < 2)
                                    // remove the whole document!
                                    theSubmodel.submodelElements.Remove(smcDoc);
                                else
                                    // remove only the document version
                                    e.SourceElementsDocument.Remove(smcVer);

                                // switch back to docs
                                // change back
                                OuterTabControl.SelectedItem = TabPanelList;

                                // re-display
                                ParseSubmodelToListItems(
                                    this.theSubmodel, this.theOptions, theViewModel.TheSelectedDocClass,
                                    theViewModel.TheSelectedLanguage, theViewModel.TheSelectedListType);

                                // re-display also in Explorer
                                var evt = new AasxPluginResultEventRedrawAllElements();
                                if (theEventStack != null)
                                    theEventStack.PushEvent(evt);

                                // OK
                                return;
                            }

                        // ReSharper enable PossibleMultipleEnumeration
                    }
            }

            // save digital file
            if (tag == null && menuItemHeader == "Save file .." && e.DigitalFile?.Path.HasContent() == true)
            {
                // make a file available
                var inputFn = e.DigitalFile.Path;

                if (CheckIfPackageFile(inputFn))
                    inputFn = thePackage.MakePackageFileAvailableAsTempFile(e.DigitalFile.Path);

                if (!inputFn.HasContent())
                {
                    Log.Error("Error making digital file available. Aborting!");
                    return;
                }

                // ask for a file name
                var dlg = new Microsoft.Win32.SaveFileDialog();
                dlg.Title = "Save digital file as ..";
                dlg.FileName = System.IO.Path.GetFileName(e.DigitalFile.Path);
                dlg.DefaultExt = "*" + System.IO.Path.GetExtension(e.DigitalFile.Path);
                dlg.Filter = "All files (*.*)|*.*";

                if (true != dlg.ShowDialog())
                    return;

                // save
                try
                {
                    File.Copy(inputFn, dlg.FileName);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "while saveing digital file to user specified loacation");
                }
            }

            // check for a document reference
            if (tag != null && tag is Tuple<DocumentEntity.DocRelationType, AdminShell.Reference> reltup
                && reltup.Item2 != null && reltup.Item2.Count > 0)
            {
                var evt = new AasxPluginResultEventNavigateToReference();
                evt.targetReference = new AdminShell.Reference(reltup.Item2);
                this.theEventStack.PushEvent(evt);
            }
        }

        private void DocumentEntity_DoubleClick(DocumentEntity e)
        {
            // first check
            if (e == null || e.DigitalFile?.Path == null || e.DigitalFile.Path.Trim().Length < 1
                || this.theEventStack == null)
                return;

            try
            {
                // temp input
                var inputFn = e.DigitalFile.Path;
                try
                {
                    if (!inputFn.ToLower().Trim().StartsWith("http://")
                            && !inputFn.ToLower().Trim().StartsWith("https://"))
                        inputFn = thePackage.MakePackageFileAvailableAsTempFile(inputFn);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Making local file available");
                }

                // give over to event stack
                var evt = new AasxPluginResultEventDisplayContentFile();
                evt.fn = inputFn;
                evt.mimeType = e.DigitalFile.MimeType;
                this.theEventStack.PushEvent(evt);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "when double-click");
            }
        }

        protected bool _inDragStart = false;

        private void DocumentEntity_DragStart(DocumentEntity e)
        {
            // first check
            if (e == null || e.DigitalFile?.Path == null || e.DigitalFile.Path.Trim().Length < 1 || _inDragStart)
            {
                _inDragStart = false;
                return;
            }

            // lock
            _inDragStart = true;

            // hastily prepare data
            try
            {
                // make a file available
                var inputFn = e.DigitalFile.Path;

                // check if it an address in the package only
                if (!inputFn.Trim().StartsWith("/"))
                {
                    Log.Error("Can only drag package local files!");
                    _inDragStart = false;
                    return;
                }

                // now should make available
                if (CheckIfPackageFile(inputFn))
                    inputFn = thePackage.MakePackageFileAvailableAsTempFile(e.DigitalFile.Path, keepFilename: true);

                if (!inputFn.HasContent())
                {
                    Log.Error("Error making digital file available. Aborting!");
                    return;
                }

                // Package the data.
                DataObject data = new DataObject();
                data.SetFileDropList(new System.Collections.Specialized.StringCollection() { inputFn });

                // Inititate the drag-and-drop operation.
                DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "when initiate file dragging");
                _inDragStart = false;
                return;
            }

            // unlock
            _inDragStart = false;
        }

        #endregion

        protected FormDescSubmodelElementCollection currentFormDescription = null;
        protected FormInstanceSubmodelElementCollection currentFormInst = null;

        protected bool formInUpdateMode = false;
        protected AdminShell.SubmodelElementWrapperCollection updateSourceElements = null;


        private void ButtonTabPanels_Click(object sender, RoutedEventArgs e)
        {
            if (sender == ButtonCreateDoc)
            {
                // show the edit panel
                OuterTabControl.SelectedItem = TabPanelEdit;
                ButtonAddUpdateDoc.Content = "Add";

                //// TODO (MIHO, 2020-09-29): if the V1.1 template works and is adopted, the old
                //// V1.0 shall be removed completely (over complicated) */
                //// make a template description for the content (remeber it)
                var desc = DocuShelfSemanticConfig.CreateVdi2770TemplateDesc(theOptions);

                // latest version (from resources)
                if (this.CheckBoxLatestVersion.IsChecked == true)
                {
                    desc = DocuShelfSemanticConfig.CreateVdi2770v11TemplateDesc();
                }

                this.currentFormDescription = desc;
                formInUpdateMode = false;
                updateSourceElements = null;

                // take over existing data
                this.currentFormInst = new FormInstanceSubmodelElementCollection(null, currentFormDescription);
                this.currentFormInst.PresetInstancesBasedOnSource(updateSourceElements);
                this.currentFormInst.outerEventStack = theEventStack;

                // bring it to the panel
                var elementsCntl = new FormListOfDifferentControl();
                elementsCntl.ShowHeader = false;
                elementsCntl.DataContext = this.currentFormInst;
                ScrollViewerForm.Content = elementsCntl;
            }

            if (sender == ButtonAddUpdateDoc)
            {
                // add
                if (this.currentFormInst != null && this.currentFormDescription != null
                    && thePackage != null
                    && theOptions != null && _semConfig.SemIdDocument != null
                    && theSubmodel != null)
                {
                    // on this level of the hierarchy, shall a new SMEC be created or shall
                    // the existing source of elements be used?
                    AdminShell.SubmodelElementWrapperCollection currentElements = null;
                    if (formInUpdateMode && updateSourceElements != null)
                    {
                        currentElements = updateSourceElements;
                    }
                    else
                    {
                        currentElements = new AdminShell.SubmodelElementWrapperCollection();
                    }

                    // create a sequence of SMEs
                    try
                    {
                        this.currentFormInst.AddOrUpdateDifferentElementsToCollection(
                            currentElements, thePackage, addFilesToPackage: true);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "when adding Document");
                    }

                    // the InstSubmodel, which started the process, should have a "fresh" SMEC available
                    // make it unique in the Documentens Submodel
                    var newSmc = this.currentFormInst?.sme as AdminShell.SubmodelElementCollection;

                    // if not update, put them into the Document's Submodel
                    if (!formInUpdateMode && currentElements != null && newSmc != null)
                    {
                        // make newSmc unique in the cotext of the Submodel
                        FormInstanceHelper.MakeIdShortUnique(theSubmodel.submodelElements, newSmc);

                        // add the elements
                        newSmc.value = currentElements;

                        // add the whole SMC
                        theSubmodel.Add(newSmc);
                    }
                }
                else
                {
                    Log.Error("Preconditions for adding Document not met.");
                }

                // change back
                OuterTabControl.SelectedItem = TabPanelList;

                // re-display
                ParseSubmodelToListItems(
                    this.theSubmodel, this.theOptions, theViewModel.TheSelectedDocClass,
                    theViewModel.TheSelectedLanguage, theViewModel.TheSelectedListType);

                // re-display also in Explorer
                var evt = new AasxPluginResultEventRedrawAllElements();
                if (theEventStack != null)
                    theEventStack.PushEvent(evt);
            }

            if (sender == ButtonCancel)
            {
                OuterTabControl.SelectedItem = TabPanelList;
            }

            if (sender == ButtonFixCDs)
            {
                // check if CDs are present
                var theDefs = new AasxPredefinedConcepts.DefinitionsVDI2770.SetOfDefsVDI2770(
                    new AasxPredefinedConcepts.DefinitionsVDI2770());
                var theCds = theDefs.GetAllReferables().Where(
                    (rf) => { return rf is AdminShell.ConceptDescription; }).ToList();

                // v11
                if (CheckBoxLatestVersion.IsChecked == true)
                {
                    theCds = AasxPredefinedConcepts.VDI2770v11.Static.GetAllReferables().Where(
                    (rf) => { return rf is AdminShell.ConceptDescription; }).ToList();
                }

                if (theCds.Count < 1)
                {
                    Log.Error(
                        "Not able to find appropriate ConceptDescriptions in pre-definitions. " +
                        "Aborting.");
                    return;
                }

                // check for Environment
                var env = this.thePackage?.AasEnv;
                if (env == null)
                {
                    Log.Error(
                        "Not able to access AAS environment for set of Submodel's ConceptDescriptions. Aborting.");
                    return;
                }

                // be safe?
                if (MessageBoxResult.Yes != MessageBox.Show(
                    "Add missing ConceptDescriptions to the AAS?", "Question",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning))
                    return;

                // ok, check
                int nr = 0;
                foreach (var x in theCds)
                {
                    var cd = x as AdminShell.ConceptDescription;
                    if (cd == null || cd.identification == null)
                        continue;
                    var cdFound = env.FindConceptDescription(cd.identification);
                    if (cdFound != null)
                        continue;
                    // ok, add
                    var newCd = new AdminShell.ConceptDescription(cd);
                    env.ConceptDescriptions.Add(newCd);
                    nr++;
                }

                // ok
                Log.Info("In total, {0} ConceptDescriptions were added to the AAS environment.", nr);
            }

            if (sender == ButtonCreateEntity)
            {
                // show the edit panel
                OuterTabControl.SelectedItem = TabPanelEntity;
            }

            if (sender == ButtonCancelEntity)
            {
                OuterTabControl.SelectedItem = TabPanelList;
            }

            if (sender == ButtonAddEntity
                && this.theSubmodel != null
                && TextBoxEntityIdShort.Text.Trim().HasContent())
            {
                // add entity
                this.theSubmodel.SmeForWrite.CreateSMEForCD<AdminShell.Entity>(
                    AasxPredefinedConcepts.VDI2770v11.Static.CD_DocumentedEntity,
                    idShort: "" + TextBoxEntityIdShort.Text.Trim(),
                    addSme: true);

                // switch back
                OuterTabControl.SelectedItem = TabPanelList;

                // re-display also in Explorer
                var evt = new AasxPluginResultEventRedrawAllElements();
                if (theEventStack != null)
                    theEventStack.PushEvent(evt);
            }
        }
    }
}
