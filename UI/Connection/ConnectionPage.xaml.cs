using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using System;
using WDCableWUI.Services;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace WDCableWUI.UI.Connection
{
    /// <summary>
    /// Converter to convert boolean values to Visibility enum values.
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool boolValue = value is bool && (bool)value;
            bool inverse = parameter?.ToString() == "Inverse";
            
            if (inverse)
                boolValue = !boolValue;
                
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>
    /// Page for managing WiFi Direct connections.
    /// </summary>
    public sealed partial class ConnectionPage : Page
    {
        private WiFiDirectService? _wifiDirectService;
        private WiFiDirectService? _subscribedWiFiDirectService;
        private readonly DispatcherQueue _dispatcherQueue;
        private bool _isInitialized = false;

        public ConnectionPage()
        {
            InitializeComponent();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            Unloaded += OnPageUnloaded;
        }

        private void InitializeWiFiDirectService()
        {
            try
            {
                UnsubscribeFromWiFiDirectEvents();
                _isInitialized = false;

                // Get the singleton WiFiDirectService from ServiceManager with null checks
                _wifiDirectService = ServiceManager.AreWiFiDirectServicesAvailable ? ServiceManager.WiFiDirectService : null;
                
                if (_wifiDirectService != null)
                {
                    // Bind device list to ListView
                    DeviceListView.ItemsSource = _wifiDirectService.DiscoveredDevices;
                    DeviceListView.IsEnabled = true;
                    RetryDiscoverableButton.IsEnabled = true;
                    UpdateDiscoverabilityStatus();
                    LastStatusText.Text = "WiFi Direct service ready";
                    _isInitialized = true;
                }
                else
                {
                    ApplyUnavailableState(ServiceManager.ServiceUnavailableMessage);
                }
            }
            catch (Exception ex)
            {
                _wifiDirectService = null;
                ApplyUnavailableState($"Failed to initialize WiFi Direct service: {ex.Message}");
            }
        }

        private void SubscribeToWiFiDirectEvents()
        {
            if (_wifiDirectService == null || _subscribedWiFiDirectService == _wifiDirectService)
            {
                return;
            }

            UnsubscribeFromWiFiDirectEvents();

            _wifiDirectService.DeviceDiscovered += OnDeviceDiscovered;
            _wifiDirectService.DeviceConnected += OnDeviceConnected;
            _wifiDirectService.DeviceDisconnected += OnDeviceDisconnected;
            _wifiDirectService.StatusChanged += OnStatusChanged;
            _wifiDirectService.ErrorOccurred += OnErrorOccurred;
            _subscribedWiFiDirectService = _wifiDirectService;
        }

        private void UnsubscribeFromWiFiDirectEvents()
        {
            if (_subscribedWiFiDirectService == null)
            {
                return;
            }

            _subscribedWiFiDirectService.DeviceDiscovered -= OnDeviceDiscovered;
            _subscribedWiFiDirectService.DeviceConnected -= OnDeviceConnected;
            _subscribedWiFiDirectService.DeviceDisconnected -= OnDeviceDisconnected;
            _subscribedWiFiDirectService.StatusChanged -= OnStatusChanged;
            _subscribedWiFiDirectService.ErrorOccurred -= OnErrorOccurred;
            _subscribedWiFiDirectService = null;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            InitializeWiFiDirectService();
            SubscribeToWiFiDirectEvents();
            UpdateUI();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            // Stop scanning when navigating away to save battery
            if (_wifiDirectService?.IsScanning == true)
            {
                _ = _wifiDirectService.StopScanAsync("connection_page_navigated_from", clearDevices: false);
            }

            UnsubscribeFromWiFiDirectEvents();
        }

        private async void OnScanButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            try
            {
                var success = _wifiDirectService != null ? await _wifiDirectService.StartScanAsync("user_scan") : false;
                if (success)
                {
                    ScanButton.IsEnabled = false;
                    StopScanButton.IsEnabled = true;
                    EmptyStatePanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ShowError("Failed to start scanning");
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error starting scan: {ex.Message}");
            }
        }

        private async void OnStopScanButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            try
            {
                if (_wifiDirectService != null)
                {
                    await _wifiDirectService.StopScanAsync("user_stop_scan", clearDevices: true);
                }
                ScanButton.IsEnabled = true;
                StopScanButton.IsEnabled = false;
                
                if (_wifiDirectService?.DiscoveredDevices.Count == 0)
                {
                    EmptyStatePanel.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error stopping scan: {ex.Message}");
            }
        }

        private async void OnConnectButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            
            if (sender is not Button button || button.Tag is not WiFiDirectDevice device || device.IsConnected)
            {
                return;
            }

            // Disable the button to prevent multiple clicks
            button.IsEnabled = false;

            try
            {
                // Show connecting status
                device.Status = "Connecting...";
                
                var success = _wifiDirectService != null ? await _wifiDirectService.ConnectToDeviceAsync(device) : false;
                if (!success)
                {
                    device.Status = "Connection failed";
                    ShowError($"Failed to connect to {device.Name}. {WiFiDirectService.DeviceBusyRecoveryHint}");
                }
            }
            catch (Exception ex)
            {
                device.Status = "Connection failed";
                ShowError($"Error connecting to device: {ex.Message}");
            }
            finally
            {
                // Re-enable the button after connection attempt completes
                // Only re-enable if the device is not connected (connection failed)
                if (!device.IsConnected)
                {
                    button.IsEnabled = true;
                }
            }
        }

        private async void OnDisconnectDeviceButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            
            var button = sender as Button;
            var device = button?.Tag as WiFiDirectDevice;
            if (device == null || !device.IsConnected) return;

            try
            {
                await (_wifiDirectService?.DisconnectAsync() ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                ShowError($"Error disconnecting from device: {ex.Message}");
            }
        }

        private async void OnDisconnectButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            try
            {
                await (_wifiDirectService?.DisconnectAsync() ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                ShowError($"Error disconnecting: {ex.Message}");
            }
        }

        private void OnDeviceDiscovered(object? sender, WiFiDirectDevice device)
        {
            _dispatcherQueue.TryEnqueue(() => {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
            });
        }

        private void OnDeviceConnected(object? sender, WiFiDirectDevice device)
        {
            _dispatcherQueue.TryEnqueue(() => {
                UpdateConnectionStatus($"Connected to {device.Name}", GetRoleDiagnostics());
                DisconnectButton.IsEnabled = true;
                ScanButton.IsEnabled = !(_wifiDirectService?.IsScanning ?? false);
                StopScanButton.IsEnabled = _wifiDirectService?.IsScanning ?? false;
            });
        }

        private void OnDeviceDisconnected(object? sender, EventArgs e)
        {
            _dispatcherQueue.TryEnqueue(() => {
                UpdateConnectionStatus("Not connected", "");
                DisconnectButton.IsEnabled = false;
            });
        }

        private void OnStatusChanged(object? sender, string status)
        {
            _dispatcherQueue.TryEnqueue(() => {
                LastStatusText.Text = status;
                UpdateDiscoverabilityStatus();
                if (_wifiDirectService?.IsConnected == true &&
                    (status.Contains("Role") || status.Contains("endpoint") || status.Contains("Connected")))
                {
                    DeviceRoleText.Text = GetRoleDiagnostics();
                }
            });
        }

        private void OnErrorOccurred(object? sender, string error)
        {
            _dispatcherQueue.TryEnqueue(() => {
                LastStatusText.Text = error;
                UpdateDiscoverabilityStatus();
                ShowError(error);
            });
        }

        private async void OnRetryDiscoverableButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _wifiDirectService == null)
            {
                return;
            }

            try
            {
                RetryDiscoverableButton.IsEnabled = false;
                var success = await _wifiDirectService.EnsureDiscoverableAsync("user_retry");
                if (!success)
                {
                    ShowError(_wifiDirectService.DiscoverabilityStatus);
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error retrying discoverability: {ex.Message}");
            }
            finally
            {
                UpdateDiscoverabilityStatus();
            }
        }

        private void UpdateConnectionStatus(string status, string role)
        {
            ConnectionStatusText.Text = status;
            DeviceRoleText.Text = role;
        }

        private void UpdateUI()
        {
            if (!_isInitialized)
            {
                ApplyUnavailableState(ServiceManager.ServiceUnavailableMessage);
                return;
            }

            // Update button states based on current service state
            ScanButton.IsEnabled = !(_wifiDirectService?.IsScanning ?? false);
            StopScanButton.IsEnabled = _wifiDirectService?.IsScanning ?? false;
            DisconnectButton.IsEnabled = _wifiDirectService?.IsConnected ?? false;
            UpdateDiscoverabilityStatus();
            
            // Update connection status
            if (_wifiDirectService?.IsConnected == true)
            {
                UpdateConnectionStatus($"Connected to {_wifiDirectService?.ConnectedDevice?.Name}",
                    GetRoleDiagnostics());
            }
            else
            {
                UpdateConnectionStatus("Not connected", "");
            }
            
            // Update empty state visibility
            EmptyStatePanel.Visibility = (_wifiDirectService?.DiscoveredDevices.Count ?? 0) == 0 ? 
                Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyUnavailableState(string message)
        {
            _isInitialized = false;
            DeviceListView.ItemsSource = null;
            DeviceListView.IsEnabled = false;
            DiscoverabilityStatusText.Text = "Discoverable: Unavailable";
            RetryDiscoverableButton.IsEnabled = false;
            ScanButton.IsEnabled = false;
            StopScanButton.IsEnabled = false;
            DisconnectButton.IsEnabled = false;
            EmptyStatePanel.Visibility = Visibility.Visible;
            LastStatusText.Text = message;
            UpdateConnectionStatus("WiFi Direct unavailable", message);
        }

        private string GetRoleDiagnostics()
        {
            return _wifiDirectService?.EndpointDiagnostics ?? string.Empty;
        }

        private void UpdateDiscoverabilityStatus()
        {
            if (_wifiDirectService == null)
            {
                DiscoverabilityStatusText.Text = "Discoverable: Unavailable";
                RetryDiscoverableButton.IsEnabled = false;
                return;
            }

            DiscoverabilityStatusText.Text = _wifiDirectService.DiscoverabilityStatus;
            RetryDiscoverableButton.IsEnabled = !_wifiDirectService.IsAdvertising;
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeFromWiFiDirectEvents();
        }

        private async void ShowError(string message)
        {
            try
            {
                var dialog = new ContentDialog()
                {
                    Title = "WiFi Direct Error",
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                
                await dialog.ShowAsync();
            }
            catch
            {
                // Fallback if dialog fails
                System.Diagnostics.Debug.WriteLine($"WiFi Direct Error: {message}");
            }
        }

    }
}
