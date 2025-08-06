using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI;
using Windows.UI;
using WDCableWUI.Services;

namespace WDCableWUI.UI.FileTransfer
{


    /// <summary>
    /// Data model for transfer records
    /// </summary>
    public class TransferRecord
    {
        public string FileName { get; set; }
        public string Status { get; set; }
        public string TimeStamp { get; set; }
        public string TypeIcon { get; set; }
        public SolidColorBrush StatusColor { get; set; }
        public double Progress { get; set; }
        public Visibility ProgressVisibility { get; set; }
        public TransferType Type { get; set; }
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
        private ObservableCollection<TransferRecord> _transferRecords;
        private ObservableCollection<TransferRecord> _filteredRecords;
        private string _currentFilter = "All";
        private StorageFile? _selectedFile = null;
        private FileTransferService? _fileTransferService;

        public FileTransferPage()
        {
            InitializeComponent();
            InitializeCollections();
            InitializeDefaultSettings();
            InitializeFileTransferService();
        }

        private void InitializeCollections()
        {
            _transferRecords = new ObservableCollection<TransferRecord>();
            _filteredRecords = new ObservableCollection<TransferRecord>();
            
            TransferRecordsList.ItemsSource = _filteredRecords;

        }

        private void InitializeDefaultSettings()
        {
            // Set default download location to user's Downloads folder
            var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            DownloadLocationTextBox.Text = downloadsPath;
            
            // Update the service with the default path
            UpdateServiceDownloadPath(downloadsPath);
        }
        
        private void InitializeFileTransferService()
        {
            try
            {
                _fileTransferService = ServiceManager.IsInitialized ? ServiceManager.FileTransferService : null;
                if (_fileTransferService != null)
                {
                    SubscribeToFileTransferEvents();
                }
            }
            catch (Exception ex)
            {
                // Service manager might not be initialized yet
                System.Diagnostics.Debug.WriteLine($"FileTransferService not available: {ex.Message}");
                _fileTransferService = null;
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
            if (_fileTransferService != null)
            {
                _fileTransferService.FileSent += OnFileSent;
                _fileTransferService.FileReceived += OnFileReceived;
                _fileTransferService.FileReceiveStarted += OnFileReceiveStarted;
                _fileTransferService.TransferProgress += OnTransferProgress;
                _fileTransferService.StatusChanged += OnFileTransferStatusChanged;
                _fileTransferService.ErrorOccurred += OnFileTransferError;
            }
        }
        
        private void UnsubscribeFromFileTransferEvents()
        {
            if (_fileTransferService != null)
            {
                _fileTransferService.FileSent -= OnFileSent;
                _fileTransferService.FileReceived -= OnFileReceived;
                _fileTransferService.FileReceiveStarted -= OnFileReceiveStarted;
                _fileTransferService.TransferProgress -= OnTransferProgress;
                _fileTransferService.StatusChanged -= OnFileTransferStatusChanged;
                _fileTransferService.ErrorOccurred -= OnFileTransferError;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Unsubscribe from events when navigating away
            UnsubscribeFromFileTransferEvents();
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
            SendFileButton.IsEnabled = _selectedFile != null && _fileTransferService?.IsConnected == true;
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
                
                // Create transfer record for the outgoing file
                var properties = await _selectedFile.GetBasicPropertiesAsync();
                
                
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
            }
        }
        
        private void DownloadLocationTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                // Validate that the path exists before updating the service
                if (Directory.Exists(textBox.Text))
                {
                    UpdateServiceDownloadPath(textBox.Text);
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
            }
        }

        #endregion

        #region File Transfer Event Handlers

        private void OnFileSent(object? sender, FileTransferEventArgs e)
        {
            
            var transferRecord = new TransferRecord
                {
                    FileName = e.FileName,
                    Status = "Sent",
                    TimeStamp = DateTime.Now.ToString("HH:mm"),
                    TypeIcon = "\uE724", // Send icon
                    StatusColor = new SolidColorBrush(Colors.Green),
                    Progress = 0,
                    ProgressVisibility = Visibility.Visible,
                    Type = TransferType.Sent
                };
                
                _transferRecords.Insert(0, transferRecord);
                ApplyFilter();
        }

        private void OnFileReceiveStarted(object? sender, FileReceiveStartedEventArgs e)
         {
             
         }

        private void OnFileReceived(object? sender, FileTransferEventArgs e)
        {
            // Find existing transfer record and update it
            var transferRecord = new TransferRecord
             {
                 FileName = e.FileName,
                 Status = "Received",
                 TimeStamp = DateTime.Now.ToString("HH:mm"),
                 TypeIcon = "\uE896", // Receive icon
                 StatusColor = new SolidColorBrush(Colors.Green),
                 Progress = 0,
                 ProgressVisibility = Visibility.Visible,
                 Type = TransferType.Received
             };
 
             _transferRecords.Insert(0, transferRecord);
             ApplyFilter();
        }

        private void OnTransferProgress(object? sender, FileTransferProgressEventArgs e)
        {

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