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

namespace WDCableWUI.UI.FileTransfer
{
    /// <summary>
    /// Data model for selected files
    /// </summary>
    public class SelectedFileItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public string SizeFormatted => FormatFileSize(Size);

        private static string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return $"{number:n1} {suffixes[counter]}";
        }
    }

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
        private ObservableCollection<SelectedFileItem> _selectedFiles;
        private ObservableCollection<TransferRecord> _transferRecords;
        private ObservableCollection<TransferRecord> _filteredRecords;
        private string _currentFilter = "All";

        public FileTransferPage()
        {
            InitializeComponent();
            InitializeCollections();
            InitializeDefaultSettings();
        }

        private void InitializeCollections()
        {
            _selectedFiles = new ObservableCollection<SelectedFileItem>();
            _transferRecords = new ObservableCollection<TransferRecord>();
            _filteredRecords = new ObservableCollection<TransferRecord>();
            
            SelectedFilesList.ItemsSource = _selectedFiles;
            TransferRecordsList.ItemsSource = _filteredRecords;

            // Add some sample transfer records for demonstration
            AddSampleTransferRecords();
        }

        private void InitializeDefaultSettings()
        {
            // Set default download location to user's Downloads folder
            var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            DownloadLocationTextBox.Text = downloadsPath;
        }

        private void AddSampleTransferRecords()
        {
            var sampleRecords = new List<TransferRecord>
            {
                new TransferRecord
                {
                    FileName = "document.pdf",
                    Status = "Completed",
                    TimeStamp = DateTime.Now.AddMinutes(-30).ToString("HH:mm"),
                    TypeIcon = "\uE724", // Send icon
                    StatusColor = new SolidColorBrush(Colors.Green),
                    Progress = 100,
                    ProgressVisibility = Visibility.Collapsed,
                    Type = TransferType.Sent
                },
                new TransferRecord
                {
                    FileName = "image.jpg",
                    Status = "Completed",
                    TimeStamp = DateTime.Now.AddHours(-1).ToString("HH:mm"),
                    TypeIcon = "\uE896", // Receive icon
                    StatusColor = new SolidColorBrush(Colors.Green),
                    Progress = 100,
                    ProgressVisibility = Visibility.Collapsed,
                    Type = TransferType.Received
                },
                new TransferRecord
                {
                    FileName = "video.mp4",
                    Status = "Transferring",
                    TimeStamp = DateTime.Now.ToString("HH:mm"),
                    TypeIcon = "\uE724", // Send icon
                    StatusColor = new SolidColorBrush(Colors.Orange),
                    Progress = 65,
                    ProgressVisibility = Visibility.Visible,
                    Type = TransferType.Sent
                }
            };

            foreach (var record in sampleRecords)
            {
                _transferRecords.Add(record);
            }
            
            ApplyFilter();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }

        #region File Selection Events

        private async void BrowseFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            
            // Initialize the picker with the window handle
            var window = (Application.Current as App)?.Window ?? Microsoft.UI.Xaml.Window.Current;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                await AddFilesToSelection(files);
            }
        }

        private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
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
                var files = await folder.GetFilesAsync();
                await AddFilesToSelection(files);
            }
        }

        private async Task AddFilesToSelection(IEnumerable<StorageFile> files)
        {
            foreach (var file in files)
            {
                var properties = await file.GetBasicPropertiesAsync();
                var fileItem = new SelectedFileItem
                {
                    Name = file.Name,
                    FullPath = file.Path,
                    Size = (long)properties.Size
                };

                // Check if file is already selected
                if (!_selectedFiles.Any(f => f.FullPath == fileItem.FullPath))
                {
                    _selectedFiles.Add(fileItem);
                }
            }

            UpdateSelectedFilesVisibility();
        }

        private void UpdateSelectedFilesVisibility()
        {
            SelectedFilesPanel.Visibility = _selectedFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RemoveFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SelectedFileItem fileItem)
            {
                _selectedFiles.Remove(fileItem);
                UpdateSelectedFilesVisibility();
            }
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
                var files = items.OfType<StorageFile>();
                
                if (files.Any())
                {
                    await AddFilesToSelection(files);
                }
            }
        }

        #endregion

        #region Transfer Operations

        private async void SendFilesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFiles.Count == 0)
            {
                await ShowMessageDialog("No files selected", "Please select files to send first.");
                return;
            }

            // TODO: Implement actual file sending logic with FileTransferService
            // For now, simulate the transfer
            await SimulateFileTransfer();
        }

        private async Task SimulateFileTransfer()
        {
            foreach (var file in _selectedFiles.ToList())
            {
                var transferRecord = new TransferRecord
                {
                    FileName = file.Name,
                    Status = "Transferring",
                    TimeStamp = DateTime.Now.ToString("HH:mm"),
                    TypeIcon = "\uE724", // Send icon
                    StatusColor = new SolidColorBrush(Colors.Orange),
                    Progress = 0,
                    ProgressVisibility = Visibility.Visible,
                    Type = TransferType.Sent
                };

                _transferRecords.Insert(0, transferRecord);
                ApplyFilter();

                // Simulate progress
                for (int progress = 0; progress <= 100; progress += 10)
                {
                    transferRecord.Progress = progress;
                    await Task.Delay(100); // Simulate transfer time
                }

                // Mark as completed
                transferRecord.Status = "Completed";
                transferRecord.StatusColor = new SolidColorBrush(Colors.Green);
                transferRecord.ProgressVisibility = Visibility.Collapsed;
            }

            // Clear selected files after sending
            _selectedFiles.Clear();
            UpdateSelectedFilesVisibility();

            await ShowMessageDialog("Transfer Complete", "All files have been sent successfully.");
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