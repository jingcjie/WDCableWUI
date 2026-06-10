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
            throw new NotImplementedException();
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

                // Get the singleton WiFiDirectService from ServiceManager with null checks
                _wifiDirectService = ServiceManager.IsInitialized ? ServiceManager.WiFiDirectService : null;
                
                if (_wifiDirectService != null)
                {
                    // Bind device list to ListView
                    DeviceListView.ItemsSource = _wifiDirectService.DiscoveredDevices;
                    _isInitialized = true;
                }
                else
                {
                    // Service not available, show appropriate message
                    ShowError("WiFi Direct service is not available. Some features may not work.");
                }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to initialize WiFi Direct service: {ex.Message}");
                _wifiDirectService = null;
                _isInitialized = false;
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
            _wifiDirectService.ConnectionRequested += OnConnectionRequested;
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
            _subscribedWiFiDirectService.ConnectionRequested -= OnConnectionRequested;
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
                _wifiDirectService.StopScanning();
            }

            UnsubscribeFromWiFiDirectEvents();
        }

        private async void OnDiscoverableToggled(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            
            var toggle = sender as ToggleSwitch;
            if (toggle == null) return;

            try
            {
                if (toggle.IsOn)
                {
                    var success = _wifiDirectService != null ? await _wifiDirectService.StartAdvertisingAsync("WDCableWUI Device") : false;
                    if (!success)
                    {
                        toggle.IsOn = false;
                        ShowError("Failed to make device discoverable");
                    }
                }
                else
                {
                    _wifiDirectService?.StopAdvertising();
                }
            }
            catch (Exception ex)
            {
                toggle.IsOn = false;
                ShowError($"Error toggling device visibility: {ex.Message}");
            }
        }

        private async void OnScanButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            try
            {
                var success = _wifiDirectService != null ? await _wifiDirectService.StartScanningAsync() : false;
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

        private void OnStopScanButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            try
            {
                _wifiDirectService?.StopScanning();
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
                    ShowError($"Failed to connect to {device.Name}");
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
                UpdateConnectionStatus($"Connected to {device.Name}", 
                    _wifiDirectService?.IsGroupOwner == true ? "Group Owner" : "Client");
                DisconnectButton.IsEnabled = true;
                
                // Stop scanning when connected
                if (_wifiDirectService?.IsScanning == true)
                {
                    _wifiDirectService?.StopScanning();
                    ScanButton.IsEnabled = true;
                    StopScanButton.IsEnabled = false;
                }
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
                // You could add a status bar or notification here
                // For now, we'll just update the connection status if it's a connection-related message
                if (status.Contains("Group Owner") || status.Contains("Client"))
                {
                    DeviceRoleText.Text = status;
                }
            });
        }

        private void OnErrorOccurred(object? sender, string error)
        {
            _dispatcherQueue.TryEnqueue(() => {
                ShowError(error);
            });
        }
        
        private async void OnConnectionRequested(object? sender, WDCableWUI.Services.ConnectionRequestEventArgs e)
        {
            try
            {
                var result = await ShowConnectionRequestDialog(e.RequestingDevice.Name);
                e.ResponseTask.SetResult(result);
            }
            catch (Exception ex)
            {
                // If dialog fails, default to decline
                e.ResponseTask.SetResult(false);
                ShowError($"Error showing connection request dialog: {ex.Message}");
            }
        }

        private void UpdateConnectionStatus(string status, string role)
        {
            ConnectionStatusText.Text = status;
            DeviceRoleText.Text = role;
        }

        private void UpdateUI()
        {
            if (!_isInitialized) return;

            // Update button states based on current service state
            ScanButton.IsEnabled = !(_wifiDirectService?.IsScanning ?? false);
            StopScanButton.IsEnabled = _wifiDirectService?.IsScanning ?? false;
            DisconnectButton.IsEnabled = _wifiDirectService?.IsConnected ?? false;
            DiscoverableToggle.IsOn = _wifiDirectService?.IsAdvertising ?? false;
            
            // Update connection status
            if (_wifiDirectService?.IsConnected == true)
            {
                UpdateConnectionStatus($"Connected to {_wifiDirectService?.ConnectedDevice?.Name}",
                    _wifiDirectService?.IsGroupOwner == true ? "Group Owner" : "Client");
            }
            else
            {
                UpdateConnectionStatus("Not connected", "");
            }
            
            // Update empty state visibility
            EmptyStatePanel.Visibility = (_wifiDirectService?.DiscoveredDevices.Count ?? 0) == 0 ? 
                Visibility.Visible : Visibility.Collapsed;
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeFromWiFiDirectEvents();
        }

        private async Task<bool> ShowConnectionRequestDialog(string deviceName)
        {
            try
            {
                var dialog = new ContentDialog()
                {
                    Title = "Connection Request",
                    Content = $"'{deviceName}' wants to connect to your device.\n\nDo you want to accept this connection?",
                    PrimaryButtonText = "Accept",
                    SecondaryButtonText = "Decline",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };
                
                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary;
            }
            catch
            {
                // Fallback if dialog fails - default to decline for security
                System.Diagnostics.Debug.WriteLine($"Connection request dialog failed for device: {deviceName}");
                return false;
            }
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
