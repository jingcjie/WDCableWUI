using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using WDCableWUI.Services;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace WDCableWUI.UI.SpeedTest
{
    public sealed partial class SpeedTestPage : Page, INotifyPropertyChanged
    {
        private readonly ObservableCollection<SpeedTestResultViewModel> _testResults;
        private readonly DispatcherQueue _dispatcherQueue;
        private bool _isUploadTestRunning;
        private bool _isDownloadTestRunning;
        private SpeedTestService? _speedTestService;
        private ConnectionService? _connectionService;
        private readonly DataManager _dataManager;

        public event PropertyChangedEventHandler PropertyChanged;

        public SpeedTestPage()
        {
            this.InitializeComponent();
            _testResults = new ObservableCollection<SpeedTestResultViewModel>();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            TestResultsListView.ItemsSource = _testResults;
            _dataManager = DataManager.Instance;
            
            // Initialize services with null checks
            try
            {
                _speedTestService = ServiceManager.IsInitialized ? ServiceManager.SpeedTestService : null;
                _connectionService = ServiceManager.IsInitialized ? ServiceManager.ConnectionService : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize services in SpeedTestPage: {ex.Message}");
                _speedTestService = null;
                _connectionService = null;
            }
            
            // Subscribe to service events if SpeedTestService is available
            if (_speedTestService != null)
            {
                _speedTestService.StatusChanged += OnSpeedTestStatusChanged;
                _speedTestService.ErrorOccurred += OnSpeedTestErrorOccurred;
                _speedTestService.UploadCompleted += OnSpeedTestCompleted;
                _speedTestService.DownloadCompleted += OnSpeedTestCompleted;
            }
            
            // Subscribe to connection service events if available
            if (_connectionService != null)
            {
                _connectionService.StatusChanged += OnConnectionStatusChanged;
            }
            
            UpdateConnectionStatus();
            UpdateTestButtonStates();
            
            // Load speed test records on initialization
            LoadSpeedTestRecords();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UpdateConnectionStatus();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            // Save speed test records when navigating away
            SaveSpeedTestRecords();
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void OnSpeedTestStatusChanged(object sender, string status)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Update UI based on status if needed
                Debug.WriteLine($"Speed test status: {status}");
            });
        }

        private void OnSpeedTestErrorOccurred(object sender, string error)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Reset test states
                _isUploadTestRunning = false;
                _isDownloadTestRunning = false;
                UpdateTestButtonStates();
                HideProgressBars();
                
                // Log the error for debugging
                System.Diagnostics.Debug.WriteLine($"Speed test error: {error}");
            });
        }

        private void OnSpeedTestCompleted(object sender, SpeedTestResult result)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Add result to history
                var resultViewModel = new SpeedTestResultViewModel(result);
                _testResults.Insert(0, resultViewModel); // Add to top
                
                // Update speed display
                if (result.TestType == SpeedTestType.Upload)
                {
                    UploadSpeedText.Text = result.SpeedMbps.ToString("F2");
                    _isUploadTestRunning = false;
                    UploadProgressBar.Visibility = Visibility.Collapsed;
                }
                else if (result.TestType == SpeedTestType.Download)
                {
                    DownloadSpeedText.Text = result.SpeedMbps.ToString("F2");
                    _isDownloadTestRunning = false;
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                }
                
                UpdateTestButtonStates();
                UpdateEmptyState();
                
                // Auto-save speed test records after adding new result
                SaveSpeedTestRecords();
            });
        }

        private void OnConnectionStatusChanged(object sender, string status)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                UpdateConnectionStatus();
                UpdateTestButtonStates();
            });
        }

        private void UpdateConnectionStatus()
        {
            try
            {
                bool isConnected = _connectionService?.IsConnected ?? false;
                bool hasSpeedTestConnection = _speedTestService?.IsConnected ?? false;
                
                if (isConnected && hasSpeedTestConnection)
                {
                    ConnectionIcon.Glyph = "\uE774"; // Connected icon
                    ConnectionIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                    ConnectionStatusText.Text = "Connected";
                    ConnectionDetailsText.Text = "Speed test connection is ready";
                }
                else if (isConnected)
                {
                    ConnectionIcon.Glyph = "\uE783"; // Warning icon
                    ConnectionIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
                    ConnectionStatusText.Text = "Partially Connected";
                    ConnectionDetailsText.Text = "Main connection established, speed test connection pending";
                }
                else
                {
                    ConnectionIcon.Glyph = "\uE774"; // Disconnected icon
                    ConnectionIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                    ConnectionStatusText.Text = "Not Connected";
                    ConnectionDetailsText.Text = "Establish a connection to begin speed testing";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update connection status in SpeedTestPage: {ex.Message}");
                // Set safe defaults
                if (ConnectionStatusText != null) ConnectionStatusText.Text = "Service Unavailable";
                if (ConnectionDetailsText != null) ConnectionDetailsText.Text = "Speed test service is not available";
            }
        }

        private void UpdateTestButtonStates()
        {
            bool canTest = (_connectionService?.IsConnected ?? false) && (_speedTestService?.IsConnected ?? false);
            
            StartUploadButton.IsEnabled = canTest && !_isUploadTestRunning && !_isDownloadTestRunning;
            StartDownloadButton.IsEnabled = canTest && !_isDownloadTestRunning && !_isUploadTestRunning;
            
            UploadSizeComboBox.IsEnabled = !_isUploadTestRunning && !_isDownloadTestRunning;
            DownloadSizeComboBox.IsEnabled = !_isUploadTestRunning && !_isDownloadTestRunning;
            
            // Update button content
            StartUploadButton.Content = _isUploadTestRunning ? "Testing..." : "Start Upload Test";
            StartDownloadButton.Content = _isDownloadTestRunning ? "Testing..." : "Start Download Test";
        }

        private void UpdateEmptyState()
        {
            EmptyStatePanel.Visibility = _testResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideProgressBars()
        {
            UploadProgressBar.Visibility = Visibility.Collapsed;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
        }

        private async void RefreshConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            button.IsEnabled = false;
            button.Content = "Refreshing...";
            
            try
            {
                // Give some time for connection to establish
                await Task.Delay(1000);
                UpdateConnectionStatus();
                UpdateTestButtonStates();
            }
            finally
            {
                button.IsEnabled = true;
                button.Content = "Refresh";
            }
        }

        private async void StartUploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploadTestRunning || _isDownloadTestRunning || _speedTestService == null)
                return;

            var selectedItem = UploadSizeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem?.Tag is string sizeStr && int.TryParse(sizeStr, out int sizeBytes))
            {
                _isUploadTestRunning = true;
                UpdateTestButtonStates();
                
                // Show progress bar
                UploadProgressBar.Visibility = Visibility.Visible;
                UploadProgressBar.IsIndeterminate = true;
                
                try
                {
                    // Double-check service availability before starting
                    if (_speedTestService == null || !_speedTestService.IsConnected)
                    {
                        throw new InvalidOperationException("Speed test service is not available or not connected");
                    }
                    
                    await _speedTestService.PerformUploadTest(sizeBytes);
                }
                catch (Exception ex)
                {
                    _isUploadTestRunning = false;
                    UpdateTestButtonStates();
                    UploadProgressBar.Visibility = Visibility.Collapsed;
                    
                    System.Diagnostics.Debug.WriteLine($"Upload test failed: {ex.Message}");
                    
                    var dialog = new ContentDialog
                    {
                        Title = "Upload Test Failed",
                        Content = $"Failed to perform upload test:\n\n{ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
            }
        }

        private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploadTestRunning || _isDownloadTestRunning || _speedTestService == null)
                return;

            var selectedItem = DownloadSizeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem?.Tag is string sizeStr && int.TryParse(sizeStr, out int sizeBytes))
            {
                _isDownloadTestRunning = true;
                UpdateTestButtonStates();
                
                // Show progress bar
                DownloadProgressBar.Visibility = Visibility.Visible;
                DownloadProgressBar.IsIndeterminate = true;
                
                try
                {
                    // Double-check service availability before starting
                    if (_speedTestService == null || !_speedTestService.IsConnected)
                    {
                        throw new InvalidOperationException("Speed test service is not available or not connected");
                    }
                    
                    await _speedTestService.PerformDownloadTest(sizeBytes);
                }
                catch (Exception ex)
                {
                    _isDownloadTestRunning = false;
                    UpdateTestButtonStates();
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    
                    System.Diagnostics.Debug.WriteLine($"Download test failed: {ex.Message}");
                    
                    var dialog = new ContentDialog
                    {
                        Title = "Download Test Failed",
                        Content = $"Failed to perform download test:\n\n{ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
            }
        }

        private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_testResults.Count == 0)
                return;

            var dialog = new ContentDialog
            {
                Title = "Clear Test History",
                Content = "Are you sure you want to clear all test results? This action cannot be undone.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _testResults.Clear();
                UploadSpeedText.Text = "0.00";
                DownloadSpeedText.Text = "0.00";
                UpdateEmptyState();
                
                // Clear persistent storage as well
                _dataManager.ClearSpeedTestRecords();
            }
        }
        
        /// <summary>
        /// Saves the current speed test records to persistent storage.
        /// </summary>
        private void SaveSpeedTestRecords()
        {
            try
            {
                var recordData = _testResults.Select(vm => new SpeedTestRecordData
                {
                    TestType = (int)vm.Result.TestType,
                    DataSize = vm.Result.DataSize,
                    DurationMs = vm.Result.Duration.TotalMilliseconds,
                    SpeedMbps = vm.Result.SpeedMbps,
                    Success = vm.Result.Success,
                    ErrorMessage = vm.Result.ErrorMessage,
                    Timestamp = DateTime.Now // Use current time as we don't store original timestamp
                }).ToList();
                
                _dataManager.SaveSpeedTestRecords(recordData);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save speed test records: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Loads speed test records from persistent storage and displays them.
        /// </summary>
        private void LoadSpeedTestRecords()
        {
            try
            {
                var recordData = _dataManager.LoadSpeedTestRecords();
                
                foreach (var data in recordData)
                {
                    var result = new SpeedTestResult
                    {
                        TestType = (SpeedTestType)data.TestType,
                        DataSize = data.DataSize,
                        Duration = TimeSpan.FromMilliseconds(data.DurationMs),
                        SpeedMbps = data.SpeedMbps,
                        Success = data.Success,
                        ErrorMessage = data.ErrorMessage
                    };
                    
                    var resultViewModel = new SpeedTestResultViewModel(result, data.Timestamp);
                    _testResults.Add(resultViewModel);
                }
                
                UpdateEmptyState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load speed test records: {ex.Message}");
            }
        }
    }

    // ViewModel for displaying speed test results
    public class SpeedTestResultViewModel
    {
        public SpeedTestResult Result { get; }
        public DateTime TestTimestamp { get; }

        public SpeedTestResultViewModel(SpeedTestResult result, DateTime? timestamp = null)
        {
            Result = result;
            TestTimestamp = timestamp ?? DateTime.Now;
        }

        public string TestTypeIcon => Result.TestType == SpeedTestType.Upload ? "\uE898" : "\uE896";
        public string TestTypeName => Result.TestType == SpeedTestType.Upload ? "Upload" : "Download";
        public string Timestamp => TestTimestamp.ToString("HH:mm:ss");
        public string DataSizeFormatted => FormatBytes(Result.DataSize);
        public string DurationFormatted => $"{Result.Duration.TotalMilliseconds:F0}ms";
        public string SpeedFormatted => $"{Result.SpeedMbps:F2} Mbps";

        private static string FormatBytes(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            if (bytes >= GB)
                return $"{(double)bytes / GB:F1} GB";
            if (bytes >= MB)
                return $"{(double)bytes / MB:F1} MB";
            if (bytes >= KB)
                return $"{(double)bytes / KB:F1} KB";
            return $"{bytes} B";
        }
    }
}