using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.WiFiDirect;
using Windows.Devices.Enumeration;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Storage.Streams;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
namespace WDCableWUI.Services
{
    public class WiFiDirectDevice
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DeviceInformation? DeviceInfo { get; set; }
        public bool IsConnected { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class ConnectionRequestEventArgs : EventArgs
    {
        public WiFiDirectDevice RequestingDevice { get; set; }
        public TaskCompletionSource<bool> ResponseTask { get; set; }
        
        public ConnectionRequestEventArgs(WiFiDirectDevice device)
        {
            RequestingDevice = device;
            ResponseTask = new TaskCompletionSource<bool>();
        }
    }

    public class WiFiDirectService
    {
        private WiFiDirectAdvertisementPublisher? _publisher;
        private DeviceWatcher? _deviceWatcher;
        private WiFiDirectDevice? _connectedDevice;
        private WiFiDirectConnectionListener? _connectionListener;
        private Windows.Devices.WiFiDirect.WiFiDirectDevice? _wifiDirectDevice;
        private readonly DispatcherQueue _dispatcherQueue;
        
        public ObservableCollection<WiFiDirectDevice> DiscoveredDevices { get; }
        public bool IsAdvertising { get; private set; }
        public bool IsScanning { get; private set; }
        public bool IsConnected => _connectedDevice != null;
        public WiFiDirectDevice? ConnectedDevice => _connectedDevice;
        public bool IsGroupOwner { get; private set; }
        public string? LocalIP { get; private set; }
        public string? RemoteIP { get; private set; }

        
        public event EventHandler<WiFiDirectDevice>? DeviceDiscovered;
        public event EventHandler<WiFiDirectDevice>? DeviceConnected;
        public event EventHandler? DeviceDisconnected;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<ConnectionRequestEventArgs>? ConnectionRequested;

        public WiFiDirectService()
        {
            DiscoveredDevices = new ObservableCollection<WiFiDirectDevice>();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        public Task<bool> StartAdvertisingAsync(string deviceName = "WDCableWUI Device")
        {
            try
            {
                if (IsAdvertising) return Task.FromResult(true);

                _publisher = new WiFiDirectAdvertisementPublisher();
                _publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                _publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
                _publisher.StatusChanged += OnAdvertisementStatusChanged;
                
                // Set device name
                if (!string.IsNullOrEmpty(deviceName))
                {
                    _publisher.Advertisement.LegacySettings.Ssid = deviceName;
                }

                // Start connection listener
                _connectionListener = new WiFiDirectConnectionListener();
                _connectionListener.ConnectionRequested += OnConnectionRequested;
                
                _publisher.Start();
                IsAdvertising = true;
                
                OnStatusChanged("Device is now discoverable");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to start advertising: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        public void StopAdvertising()
        {
            try
            {
                if (!IsAdvertising) return;

                _publisher?.Stop();
                // WiFiDirectConnectionListener doesn't have Dispose method
                _publisher = null;
                _connectionListener = null;
                IsAdvertising = false;
                
                OnStatusChanged("Device is no longer discoverable");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error stopping advertising: {ex.Message}");
            }
        }

        public Task<bool> StartScanningAsync()
        {
            try
            {
                if (IsScanning) return Task.FromResult(true);
                var deviceSelector = Windows.Devices.WiFiDirect.WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
                _deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);
                
                _deviceWatcher.Added += OnDeviceAdded;
                _deviceWatcher.Removed += OnDeviceRemoved;
                _deviceWatcher.Updated += OnDeviceUpdated;
                _deviceWatcher.EnumerationCompleted += OnEnumerationCompleted;
                
                _deviceWatcher.Start();
                IsScanning = true;
                
                OnStatusChanged("Scanning for WiFi Direct devices...");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to start scanning: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        public void StopScanning()
        {
            try
            {
                if (!IsScanning) return;

                _deviceWatcher?.Stop();
                _deviceWatcher = null;
                IsScanning = false;
                
                _dispatcherQueue.TryEnqueue(() => DiscoveredDevices.Clear());
                OnStatusChanged("Stopped scanning");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error stopping scan: {ex.Message}");
            }
        }

        public async Task<bool> ConnectToDeviceAsync(WiFiDirectDevice device)
        {
            try
            {
                if (IsConnected)
                {
                    OnErrorOccurred("Already connected to a device. Disconnect first.");
                    return false;
                }

                OnStatusChanged($"Connecting to {device.Name}...");
                
                
                var wifiDirectDevice = await Windows.Devices.WiFiDirect.WiFiDirectDevice.FromIdAsync(device.Id);
                if (wifiDirectDevice == null)
                {
                    // OnErrorOccurred("Failed to connect to WiFi Direct device");
                    return false;
                }

                _connectedDevice = device;
                _connectedDevice.IsConnected = true;
                _wifiDirectDevice = wifiDirectDevice;
                
                // Register for connection status changes
                _wifiDirectDevice.ConnectionStatusChanged += OnConnectionStatusChanged;
                
                // Determine group owner role
                await DetermineGroupOwnerAsync(_wifiDirectDevice);
                
                // ConnectionService is now managed by ServiceManager
                // No need to create it here
                
                OnDeviceConnected(_connectedDevice);
                OnStatusChanged($"Connected to {device.Name}");
                return true;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Connection error: {ex.Message}");
                return false;
            }
        }

        public Task DisconnectAsync()
        {
            try
            {
                if (_connectedDevice != null)
                {
                    _connectedDevice.IsConnected = false;
                    _connectedDevice = null;
                    
                    if (_wifiDirectDevice != null)
                    {
                        _wifiDirectDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
                        _wifiDirectDevice.Dispose();
                        _wifiDirectDevice = null;
                    }
                    
                    IsGroupOwner = false;
                    LocalIP = null;
                    RemoteIP = null;
                    
                    OnDeviceDisconnected();
                    OnStatusChanged("Disconnected");
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Disconnect error: {ex.Message}");
            }
            
            return Task.CompletedTask;
        }

        private void OnConnectionStatusChanged(Windows.Devices.WiFiDirect.WiFiDirectDevice sender, object args)
        {
            _dispatcherQueue.TryEnqueue(() => {
                if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Connected)
                {
                    OnStatusChanged("WiFi Direct connection established");
                }
                else if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Disconnected)
                {
                    OnStatusChanged("WiFi Direct connection lost");
                    _ = DisconnectAsync();
                }
            });
        }
        


        private Task DetermineGroupOwnerAsync(Windows.Devices.WiFiDirect.WiFiDirectDevice wifiDirectDevice)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[WiFiDirectService] DetermineGroupOwnerAsync called");
                // Determine group owner status by analyzing IP addresses
                var endpointPairs = wifiDirectDevice.GetConnectionEndpointPairs();
                System.Diagnostics.Debug.WriteLine($"[WiFiDirectService] Found {endpointPairs.Count} endpoint pairs");
                
                if (endpointPairs.Count > 0)
                {
                    LocalIP = endpointPairs[0].LocalHostName.ToString();
                    RemoteIP = endpointPairs[0].RemoteHostName.ToString();
                    
                    System.Diagnostics.Debug.WriteLine($"[WiFiDirectService] LocalIP: {LocalIP}, RemoteIP: {RemoteIP}");
                    
                    IsGroupOwner = DetermineGroupOwnerStatus(LocalIP, RemoteIP);
                    System.Diagnostics.Debug.WriteLine($"[WiFiDirectService] IsGroupOwner determined as: {IsGroupOwner}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[WiFiDirectService] No connection endpoints available");
                    OnErrorOccurred("Warning: No connection endpoints available for group owner detection");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WiFiDirectService] DetermineGroupOwnerAsync exception: {ex.Message}");
                OnErrorOccurred($"Error determining group owner: {ex.Message}");
                IsGroupOwner = false;
            }
            
            return Task.CompletedTask;
        }


        private bool DetermineGroupOwnerStatus(string localIP, string remoteIP)
        {
            try
            {
                // Parse IP addresses
                if (!System.Net.IPAddress.TryParse(localIP, out var localAddress) ||
                    !System.Net.IPAddress.TryParse(remoteIP, out var remoteAddress))
                {
                    OnErrorOccurred($"Warning: Could not parse IP addresses - Local: {localIP}, Remote: {remoteIP}");
                    return false;
                }

                // Get the network bytes (first 3 octets) to determine if they're in the same subnet
                var localBytes = localAddress.GetAddressBytes();
                var remoteBytes = remoteAddress.GetAddressBytes();

                // Check if both IPs are in the same private network range
                bool sameSubnet = localBytes[0] == remoteBytes[0] && 
                                 localBytes[1] == remoteBytes[1] && 
                                 localBytes[2] == remoteBytes[2];

                if (sameSubnet)
                {
                    // In Wi-Fi Direct, the group owner typically gets the lower IP address
                    // (usually .1) and acts as the DHCP server
                    // The device with the lower host part (4th octet) is likely the group owner
                    return localBytes[3] < remoteBytes[3];
                }
                else
                {
                    // If not in same subnet, check for common Wi-Fi Direct patterns
                    // Group owner often has .1 as the last octet
                    return localBytes[3] == 1;
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error determining group owner status: {ex.Message}");
                return false;
            }
        }
        
        private async void OnConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
        {
            WiFiDirectConnectionRequest? request = null;
            try
            {
                request = args.GetConnectionRequest();
                var deviceInfo = request.DeviceInformation;
                
                OnStatusChanged($"Connection request from {deviceInfo.Name}");
                
                // Create device object for the request
                var requestingDevice = new WiFiDirectDevice
                {
                    Id = deviceInfo.Id,
                    Name = deviceInfo.Name,
                    DeviceInfo = deviceInfo,
                    IsConnected = false,
                    Status = "Requesting Connection"
                };
                
                // Create event args and wait for user response
                var connectionRequestArgs = new ConnectionRequestEventArgs(requestingDevice);
                
                // Raise the event on UI thread
                _dispatcherQueue.TryEnqueue(() => OnConnectionRequested(connectionRequestArgs));
                
                // Wait for user response with timeout
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                var completedTask = await Task.WhenAny(connectionRequestArgs.ResponseTask.Task, timeoutTask);
                
                bool userAccepted = false;
                if (completedTask == connectionRequestArgs.ResponseTask.Task)
                {
                    userAccepted = await connectionRequestArgs.ResponseTask.Task;
                }
                else
                {
                    // Timeout occurred
                    OnStatusChanged($"Connection request from {deviceInfo.Name} timed out");
                    connectionRequestArgs.ResponseTask.TrySetResult(false);
                }
                
                if (userAccepted)
                {
                    try
                    {
                        var wifiDirectDevice = await Windows.Devices.WiFiDirect.WiFiDirectDevice.FromIdAsync(deviceInfo.Id);
                        if (wifiDirectDevice != null)
                        {
                            requestingDevice.IsConnected = true;
                            requestingDevice.Status = "Connected";
                            
                            _connectedDevice = requestingDevice;
                            _wifiDirectDevice = wifiDirectDevice;
                            
                            // Register for connection status changes
                            _wifiDirectDevice.ConnectionStatusChanged += OnConnectionStatusChanged;
                            
                            await DetermineGroupOwnerAsync(_wifiDirectDevice);
                            
                            // ConnectionService is now managed by ServiceManager
                // No need to create it here
                            
                            _dispatcherQueue.TryEnqueue(() => OnDeviceConnected(requestingDevice));
                        }
                    }
                    catch (Exception deviceEx)
                    {
                        OnErrorOccurred($"Error creating WiFi Direct device: {deviceEx.Message}");
                    }
                }
                else
                {
                    OnStatusChanged($"Connection request from {deviceInfo.Name} was declined");
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error handling connection request: {ex.Message}");
            }
            finally
            {
                // Properly dispose of the connection request to prevent resource leaks
                request?.Dispose();
            }
        }

        private void OnAdvertisementStatusChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
        {
            _dispatcherQueue.TryEnqueue(() => {
                OnStatusChanged($"Advertisement status: {args.Status}");
            });
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation deviceInfo)
        {
            _dispatcherQueue.TryEnqueue(() => {
                var device = new WiFiDirectDevice
                {
                    Id = deviceInfo.Id,
                    Name = deviceInfo.Name ?? "Unknown Device",
                    DeviceInfo = deviceInfo,
                    IsConnected = false,
                    Status = "Available"
                };
                
                DiscoveredDevices.Add(device);
                OnDeviceDiscovered(device);
            });
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            _dispatcherQueue.TryEnqueue(() => {
                var device = DiscoveredDevices.FirstOrDefault(d => d.Id == deviceInfoUpdate.Id);
                if (device != null)
                {
                    DiscoveredDevices.Remove(device);
                }
            });
        }

        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            _dispatcherQueue.TryEnqueue(() => {
                var device = DiscoveredDevices.FirstOrDefault(d => d.Id == deviceInfoUpdate.Id);
                if (device != null)
                {
                    // Update device properties if needed
                    device.Status = "Updated";
                }
            });
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            _dispatcherQueue.TryEnqueue(() => {
                OnStatusChanged($"Found {DiscoveredDevices.Count} devices");
            });
        }

        protected virtual void OnDeviceDiscovered(WiFiDirectDevice device)
        {
            DeviceDiscovered?.Invoke(this, device);
        }

        protected virtual void OnDeviceConnected(WiFiDirectDevice device)
        {
            System.Diagnostics.Debug.WriteLine($"[WiFiDirectService] OnDeviceConnected called for device: {device?.Name}");
            DeviceConnected?.Invoke(this, device);
        }

        protected virtual void OnDeviceDisconnected()
        {
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }

        protected virtual void OnErrorOccurred(string error)
        {
            ErrorOccurred?.Invoke(this, error);
        }
        
        protected virtual void OnConnectionRequested(ConnectionRequestEventArgs args)
        {
            ConnectionRequested?.Invoke(this, args);
        }
        


        public void Dispose()
        {
            StopAdvertising();
            StopScanning();
            
            if (_connectionListener != null)
            {
                _connectionListener.ConnectionRequested -= OnConnectionRequested;
                _connectionListener = null;
            }
            
            if (_wifiDirectDevice != null)
            {
                _wifiDirectDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _wifiDirectDevice.Dispose();
                _wifiDirectDevice = null;
            }
            
            _connectedDevice = null;
        }
    }
}