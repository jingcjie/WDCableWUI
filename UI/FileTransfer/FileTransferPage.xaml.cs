using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WDCableWUI.Services;

namespace WDCableWUI.UI.FileTransfer
{


    /// <summary>
    /// Data model for transfer records
    /// </summary>
    public class TransferRecord : INotifyPropertyChanged
    {
        private string _fileName = string.Empty;
        private string _status = string.Empty;
        private string _timeStamp = string.Empty;
        private string _typeIcon = string.Empty;
        private string _filePath = string.Empty;
        private SolidColorBrush _statusColor = new(Colors.Transparent);
        private double _progress;
        private Visibility _progressVisibility = Visibility.Collapsed;
        private TransferType _type;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string TransferId { get; set; } = string.Empty;

        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public string TimeStamp
        {
            get => _timeStamp;
            set => SetProperty(ref _timeStamp, value);
        }

        public string TypeIcon
        {
            get => _typeIcon;
            set => SetProperty(ref _typeIcon, value);
        }

        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public SolidColorBrush StatusColor
        {
            get => _statusColor;
            set => SetProperty(ref _statusColor, value);
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, Math.Min(100, Math.Max(0, value)));
        }

        public Visibility ProgressVisibility
        {
            get => _progressVisibility;
            set => SetProperty(ref _progressVisibility, value);
        }

        public TransferType Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
        }

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public enum TransferType
    {
        Sent,
        Received
    }

    /// <summary>
    /// Page for file transfer operations with connected device.
    /// </summary>
    public sealed partial class FileTransferPage : Page
    {
        private readonly ObservableCollection<TransferRecord> _transferRecords = new();
        private readonly ObservableCollection<TransferRecord> _filteredRecords = new();
        private string _currentFilter = "All";
        private StorageFile? _selectedFile = null;
        private FileTransferService? _fileTransferService;
        private FileTransferService? _subscribedFileTransferService;
        private SessionManager? _sessionManager;
        private SessionManager? _subscribedSessionManager;
        private readonly Dictionary<string, TransferRecord> _activeTransferRecords = new();

        public FileTransferPage()
        {
            InitializeComponent();
            InitializeCollections();
            InitializeDefaultSettings();
            InitializeFileTransferService();
            LoadTransferHistory();
            Unloaded += OnPageUnloaded;
        }

        private void InitializeCollections()
        {
            TransferRecordsList.ItemsSource = _filteredRecords;
        }

        private void InitializeDefaultSettings()
        {
            // Load saved download path from DataManager
            var savedDownloadPath = DataManager.Instance.GetDownloadPath();
            DownloadLocationTextBox.Text = savedDownloadPath;
            
            // Update the service with the saved path
            UpdateServiceDownloadPath(savedDownloadPath);
        }
        
        private void InitializeFileTransferService()
        {
            try
            {
                _fileTransferService = ServiceManager.AreWiFiDirectServicesAvailable ? ServiceManager.FileTransferService : null;
                _sessionManager = ServiceManager.AreWiFiDirectServicesAvailable ? ServiceManager.SessionManager : null;
            }
            catch (Exception ex)
            {
                // Service manager might not be initialized yet
                System.Diagnostics.Debug.WriteLine($"FileTransferService not available: {ex.Message}");
                _fileTransferService = null;
                _sessionManager = null;
            }
        }
        
        private void UpdateServiceDownloadPath(string downloadPath)
        {
            try
            {
                _fileTransferService?.SetDownloadPath(downloadPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update download path: {ex.Message}");
            }
        }
        
        private void SubscribeToFileTransferEvents()
        {
            if (_fileTransferService != null && _subscribedFileTransferService != _fileTransferService)
            {
                UnsubscribeFromFileTransferEvents();

                _fileTransferService.FileSent += OnFileSent;
                _fileTransferService.FileReceived += OnFileReceived;
                _fileTransferService.FileReceiveStarted += OnFileReceiveStarted;
                _fileTransferService.TransferProgress += OnTransferProgress;
                _fileTransferService.TransferFailed += OnTransferFailed;
                _fileTransferService.StatusChanged += OnFileTransferStatusChanged;
                _fileTransferService.ErrorOccurred += OnFileTransferError;
                _subscribedFileTransferService = _fileTransferService;
            }

            if (_sessionManager != null && _subscribedSessionManager != _sessionManager)
            {
                UnsubscribeFromSessionEvents();
                _sessionManager.StateChanged += OnSessionStateChanged;
                _sessionManager.SessionReady += OnSessionReady;
                _sessionManager.SessionFailed += OnSessionFailed;
                _subscribedSessionManager = _sessionManager;
            }
        }
        
        private void UnsubscribeFromFileTransferEvents()
        {
            if (_subscribedFileTransferService == null)
            {
                return;
            }

            _subscribedFileTransferService.FileSent -= OnFileSent;
            _subscribedFileTransferService.FileReceived -= OnFileReceived;
            _subscribedFileTransferService.FileReceiveStarted -= OnFileReceiveStarted;
            _subscribedFileTransferService.TransferProgress -= OnTransferProgress;
            _subscribedFileTransferService.TransferFailed -= OnTransferFailed;
            _subscribedFileTransferService.StatusChanged -= OnFileTransferStatusChanged;
            _subscribedFileTransferService.ErrorOccurred -= OnFileTransferError;
            _subscribedFileTransferService = null;
        }

        private void UnsubscribeFromSessionEvents()
        {
            if (_subscribedSessionManager == null)
            {
                return;
            }

            _subscribedSessionManager.StateChanged -= OnSessionStateChanged;
            _subscribedSessionManager.SessionReady -= OnSessionReady;
            _subscribedSessionManager.SessionFailed -= OnSessionFailed;
            _subscribedSessionManager = null;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            InitializeFileTransferService();
            SubscribeToFileTransferEvents();
            LoadTransferHistory();
            UpdateSendButtonState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Unsubscribe from events when navigating away
            UnsubscribeFromFileTransferEvents();
            UnsubscribeFromSessionEvents();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeFromFileTransferEvents();
            UnsubscribeFromSessionEvents();
        }

        #region File Selection Events

        private async void BrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            
            // Initialize the picker with the window handle
            var window = (Application.Current as App)?.Window ?? Microsoft.UI.Xaml.Window.Current;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await SetSelectedFile(file);
            }
        }

        private async Task SetSelectedFile(StorageFile file)
        {
            _selectedFile = file;
            var properties = await file.GetBasicPropertiesAsync();
            
            SelectedFileName.Text = file.Name;
            SelectedFileSize.Text = FormatFileSize((long)properties.Size);
            SelectedFilePanel.Visibility = Visibility.Visible;
            
            UpdateSendButtonState();
         }
         
         private string FormatFileSize(long bytes)
         {
             string[] sizes = { "B", "KB", "MB", "GB", "TB" };
             double len = bytes;
             int order = 0;
             while (len >= 1024 && order < sizes.Length - 1)
             {
                 order++;
                 len = len / 1024;
             }
             return $"{len:0.##} {sizes[order]}";
         }
         
         private void UpdateSendButtonState()
        {
            SendFileButton.IsEnabled = _selectedFile != null &&
                (_sessionManager?.IsReady ?? false) &&
                _fileTransferService?.IsConnected == true;
        }

        private void RemoveFileButton_Click(object sender, RoutedEventArgs e)
        {
            _selectedFile = null;
            SelectedFilePanel.Visibility = Visibility.Collapsed;
            UpdateSendButtonState();
        }

        #endregion

        #region Drag and Drop Events

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop files to add them";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }

        private async void DropArea_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var firstFile = items.OfType<StorageFile>().FirstOrDefault();
                
                if (firstFile != null)
                {
                    await SetSelectedFile(firstFile);
                }
            }
        }

        #endregion

        #region Transfer Operations

        private async void SendFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFile == null)
            {
                await ShowMessageDialog("No file selected", "Please select a file to send.");
                return;
            }
            
            if (_fileTransferService == null || !_fileTransferService.IsConnected)
            {
                await ShowMessageDialog("Connection Error", "File transfer service is not available. Please ensure devices are connected.");
                return;
            }
            
            try
            {
                // Disable send button during transfer
                SendFileButton.IsEnabled = false;

                // Send the file using the file transfer service
                await _fileTransferService.SendFileAsync(_selectedFile);
                
                // Clear selected file after sending
                _selectedFile = null;
                SelectedFilePanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("Transfer Error", $"Failed to send file: {ex.Message}");
            }
            finally
            {
                UpdateSendButtonState();
            }
        }



        #endregion

        #region Settings Events

        private async void BrowseDownloadLocationButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            
            // Initialize the picker with the window handle
            var window = (Application.Current as App)?.Window ?? Microsoft.UI.Xaml.Window.Current;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                DownloadLocationTextBox.Text = folder.Path;
                UpdateServiceDownloadPath(folder.Path);
                // Save the path to DataManager for persistence
                DataManager.Instance.SetDownloadPath(folder.Path);
            }
        }
        
        private void DownloadLocationTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                // Validate that the path exists before updating the service and saving
                if (Directory.Exists(textBox.Text))
                {
                    UpdateServiceDownloadPath(textBox.Text);
                    // Save the path to DataManager for persistence
                    DataManager.Instance.SetDownloadPath(textBox.Text);
                }
            }
        }

        #endregion

        #region Transfer Records Management

        private void FilterToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle && toggle.Tag is string filter)
            {
                // Uncheck other toggles
                ShowAllToggle.IsChecked = false;
                ShowSentToggle.IsChecked = false;
                ShowReceivedToggle.IsChecked = false;

                // Check the clicked toggle
                toggle.IsChecked = true;
                _currentFilter = filter;
                
                ApplyFilter();
            }
        }

        private void ApplyFilter()
        {
            _filteredRecords.Clear();
            
            var filteredItems = _currentFilter switch
            {
                "Sent" => _transferRecords.Where(r => r.Type == TransferType.Sent),
                "Received" => _transferRecords.Where(r => r.Type == TransferType.Received),
                _ => _transferRecords
            };

            foreach (var item in filteredItems)
            {
                _filteredRecords.Add(item);
            }
        }

        private async void ClearRecordsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog()
            {
                Title = "Clear Transfer Records",
                Content = "Are you sure you want to clear all transfer records? This action cannot be undone.",
                PrimaryButtonText = "Clear",
                SecondaryButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _transferRecords.Clear();
                _filteredRecords.Clear();
                _activeTransferRecords.Clear();
                DataManager.Instance.ClearFileTransferHistory();
            }
        }

        private async void TransferRecordsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not TransferRecord record || string.IsNullOrWhiteSpace(record.FilePath))
            {
                return;
            }

            try
            {
                if (File.Exists(record.FilePath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(record.FilePath);
                    var folderPath = Path.GetDirectoryName(record.FilePath);
                    if (string.IsNullOrWhiteSpace(folderPath))
                    {
                        return;
                    }

                    var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                    var options = new FolderLauncherOptions();
                    options.ItemsToSelect.Add(file);
                    await Launcher.LaunchFolderAsync(folder, options);
                    return;
                }

                var directory = Directory.Exists(record.FilePath)
                    ? record.FilePath
                    : Path.GetDirectoryName(record.FilePath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(directory);
                    await Launcher.LaunchFolderAsync(folder);
                    return;
                }

                await ShowMessageDialog("File Not Found", "The file location is no longer available.");
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("Open Location Failed", $"Could not open the file location:\n\n{ex.Message}");
            }
        }

        #endregion

        #region File Transfer Event Handlers

        private void OnFileSent(object? sender, FileTransferEventArgs e)
        {
            ApplyTerminalRecord(new FileTransferRecordData
            {
                TransferId = e.TransferId,
                FileName = e.FileName,
                FilePath = e.FilePath,
                FileSize = e.FileSize,
                IsSender = true,
                Status = "Sent",
                Timestamp = DateTime.Now
            }, moveToFront: true);
        }

        private void OnFileReceiveStarted(object? sender, FileReceiveStartedEventArgs e)
        {
            var transferRecord = GetOrCreateTransferRecord(e.TransferId, e.FileName, isSender: false);
            transferRecord.FileName = e.FileName;
            transferRecord.Status = "Receiving";
            transferRecord.TimeStamp = DateTime.Now.ToString("HH:mm");
            transferRecord.StatusColor = new SolidColorBrush(Colors.DodgerBlue);
            transferRecord.Progress = e.FileSize == 0 ? 100 : 0;
            transferRecord.ProgressVisibility = Visibility.Visible;
        }

        private void OnFileReceived(object? sender, FileTransferEventArgs e)
        {
            ApplyTerminalRecord(new FileTransferRecordData
            {
                TransferId = e.TransferId,
                FileName = e.FileName,
                FilePath = e.FilePath,
                FileSize = e.FileSize,
                IsSender = false,
                Status = "Received",
                Timestamp = DateTime.Now
            }, moveToFront: true);
        }

        private void OnTransferProgress(object? sender, FileTransferProgressEventArgs e)
        {
            var transferRecord = GetOrCreateTransferRecord(e.TransferId, e.FileName, e.IsSender);
            transferRecord.FileName = e.FileName;
            transferRecord.Status = e.IsSender ? "Sending" : "Receiving";
            transferRecord.TimeStamp = DateTime.Now.ToString("HH:mm");
            transferRecord.StatusColor = new SolidColorBrush(Colors.DodgerBlue);
            transferRecord.Progress = e.ProgressPercentage;
            transferRecord.ProgressVisibility = Visibility.Visible;
        }

        private void OnTransferFailed(object? sender, FileTransferFailedEventArgs e)
        {
            ApplyTerminalRecord(new FileTransferRecordData
            {
                TransferId = e.TransferId,
                FileName = e.FileName,
                FilePath = e.FilePath,
                FileSize = e.FileSize,
                IsSender = e.IsSender,
                Status = "Failed",
                Timestamp = DateTime.Now,
                ErrorMessage = e.ErrorMessage
            }, moveToFront: true);
        }

        private void LoadTransferHistory()
        {
            try
            {
                foreach (var record in DataManager.Instance.LoadFileTransferHistory())
                {
                    ApplyTerminalRecord(record, moveToFront: false, applyFilter: false);
                }

                ApplyFilter();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load transfer history: {ex.Message}");
            }
        }

        private void ApplyTerminalRecord(
            FileTransferRecordData data,
            bool moveToFront,
            bool applyFilter = true)
        {
            var key = TransferKey(data.TransferId, data.FileName, data.IsSender);
            var transferRecord = _transferRecords.FirstOrDefault(record =>
                TransferKey(record.TransferId, record.FileName, record.Type == TransferType.Sent) == key);

            if (transferRecord == null)
            {
                transferRecord = new TransferRecord();
                if (moveToFront)
                {
                    _transferRecords.Insert(0, transferRecord);
                }
                else
                {
                    _transferRecords.Add(transferRecord);
                }
            }
            else if (moveToFront)
            {
                _transferRecords.Remove(transferRecord);
                _transferRecords.Insert(0, transferRecord);
            }

            transferRecord.TransferId = data.TransferId;
            transferRecord.FileName = data.FileName;
            transferRecord.FilePath = data.Status == "Failed" ? string.Empty : data.FilePath;
            transferRecord.Status = data.Status;
            transferRecord.TimeStamp = data.Timestamp.ToString("g");
            transferRecord.TypeIcon = data.IsSender ? "\uE724" : "\uE896";
            transferRecord.StatusColor = new SolidColorBrush(
                data.Status == "Failed" ? Colors.Firebrick : Colors.Green);
            transferRecord.Progress = data.Status == "Failed" ? transferRecord.Progress : 100;
            transferRecord.ProgressVisibility = Visibility.Visible;
            transferRecord.Type = data.IsSender ? TransferType.Sent : TransferType.Received;
            _activeTransferRecords.Remove(key);

            if (applyFilter)
            {
                ApplyFilter();
            }
        }

        private TransferRecord GetOrCreateTransferRecord(string transferId, string fileName, bool isSender)
        {
            var key = TransferKey(transferId, fileName, isSender);
            if (_activeTransferRecords.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var record = new TransferRecord
            {
                TransferId = transferId,
                FileName = fileName,
                Status = isSender ? "Sending" : "Receiving",
                TimeStamp = DateTime.Now.ToString("HH:mm"),
                TypeIcon = isSender ? "\uE724" : "\uE896",
                StatusColor = new SolidColorBrush(Colors.DodgerBlue),
                Progress = 0,
                ProgressVisibility = Visibility.Visible,
                Type = isSender ? TransferType.Sent : TransferType.Received
            };

            _activeTransferRecords[key] = record;
            _transferRecords.Insert(0, record);
            ApplyFilter();
            return record;
        }

        private static string TransferKey(string transferId, string fileName, bool isSender)
        {
            var id = string.IsNullOrWhiteSpace(transferId) ? fileName : transferId;
            return $"{(isSender ? "send" : "receive")}:{id}";
        }

        private void OnFileTransferStatusChanged(object? sender, string status)
        {
            // Handle status changes if needed
            System.Diagnostics.Debug.WriteLine($"FileTransfer Status: {status}");
        }

        private void OnFileTransferError(object? sender, string error)
        {
            // Handle errors
            _ = ShowMessageDialog("Transfer Error", error);
        }

        private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(UpdateSendButtonState);
        }

        private void OnSessionReady(object? sender, SessionReadyEventArgs e)
        {
            DispatcherQueue.TryEnqueue(UpdateSendButtonState);
        }

        private void OnSessionFailed(object? sender, SessionFailedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(UpdateSendButtonState);
        }

        #endregion

        #region Helper Methods

        private async Task ShowMessageDialog(string title, string message)
        {
            var dialog = new ContentDialog()
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };

            await dialog.ShowAsync();
        }

        #endregion
    }
}
