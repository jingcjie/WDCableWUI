using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace WDCableWUI.Services
{
    public enum WiFiDirectServiceState
    {
        Unavailable,
        Idle,
        Advertising,
        Scanning,
        IncomingPrompt,
        ConnectingOutbound,
        ConnectingInbound,
        Connected,
        Disconnecting,
        Error
    }

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
        public ConnectionRequestEventArgs(WiFiDirectDevice device)
        {
            RequestingDevice = device;
            ResponseTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public WiFiDirectDevice RequestingDevice { get; }

        public TaskCompletionSource<bool> ResponseTask { get; }
    }

    public class WiFiDirectService : IDisposable
    {
        private const string DefaultDeviceName = "WDCableWUI Device";
        private static readonly TimeSpan IncomingRequestTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan EndpointReadyTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan EndpointPollDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan WatcherStopTimeout = TimeSpan.FromSeconds(3);

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        private WiFiDirectAdvertisementPublisher? _publisher;
        private WiFiDirectConnectionListener? _connectionListener;
        private DeviceWatcher? _deviceWatcher;
        private TaskCompletionSource<bool>? _watcherStoppedCompletionSource;
        private WiFiDirectDevice? _connectedDevice;
        private Windows.Devices.WiFiDirect.WiFiDirectDevice? _wifiDirectDevice;
        private int _operationCounter;
        private bool _isDisposed;
        private string _lastWatcherStatus = "Unavailable";
        private string _lastPublisherStatus = "Unavailable";

        public WiFiDirectService()
        {
            DiscoveredDevices = new ObservableCollection<WiFiDirectDevice>();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            CurrentState = WiFiDirectServiceState.Idle;
            DiscoverabilityStatus = "Discoverable: Starting";
            OnStatusChanged(
                "WiFi Direct service initialized",
                NextOperationId(),
                api: "WiFiDirectService",
                result: "initialized",
                reason: "service_created");
        }

        public ObservableCollection<WiFiDirectDevice> DiscoveredDevices { get; }

        public WiFiDirectServiceState CurrentState { get; private set; }

        public string DiscoverabilityStatus { get; private set; }

        public bool IsAdvertising { get; private set; }

        public bool IsScanning { get; private set; }

        public bool IsConnected => _connectedDevice != null;

        public WiFiDirectDevice? ConnectedDevice => _connectedDevice;

        public bool IsGroupOwner { get; private set; }

        public string? LocalIP { get; private set; }

        public string? RemoteIP { get; private set; }

        public string RoleName => IsConnected ? (IsGroupOwner ? "Group Owner" : "Client") : "Not connected";

        public string EndpointDiagnostics
        {
            get
            {
                var local = string.IsNullOrWhiteSpace(LocalIP) ? "unavailable" : LocalIP;
                var remote = string.IsNullOrWhiteSpace(RemoteIP) ? "unavailable" : RemoteIP;
                return $"Role: {RoleName}; Local endpoint: {local}; Remote endpoint: {remote}";
            }
        }

        public event EventHandler<WiFiDirectDevice>? DeviceDiscovered;
        public event EventHandler<WiFiDirectDevice>? DeviceConnected;
        public event EventHandler? DeviceDisconnected;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<ConnectionRequestEventArgs>? ConnectionRequested;

        public void SetConnectionService(ConnectionService connectionService)
        {
            if (connectionService != null)
            {
                connectionService.OtherSideNotRunningApp += OnOtherSideNotRunningApp;
            }
        }

        public Task<bool> StartAdvertisingAsync(string deviceName = DefaultDeviceName)
        {
            return EnsureDiscoverableAsync("legacy_start_advertising", deviceName);
        }

        public async Task<bool> EnsureDiscoverableAsync(string reason, string deviceName = DefaultDeviceName)
        {
            ThrowIfDisposed();

            var opId = NextOperationId();
            SetDiscoverabilityStatus("Discoverable: Starting", opId, reason);
            OnStatusChanged(
                "WiFi Direct discoverability requested",
                opId,
                api: "WiFiDirectAdvertisementPublisher.Start",
                result: "requested",
                reason: reason);

            try
            {
                EnsureConnectionListener(opId, reason);
                EnsurePublisher(deviceName, opId, reason);

                if (_publisher == null)
                {
                    SetState(WiFiDirectServiceState.Error, opId, "WiFiDirectAdvertisementPublisher", result: "missing", reason: reason);
                    SetDiscoverabilityStatus("Discoverable: Failed - WiFi Direct publisher is unavailable", opId, reason);
                    OnErrorOccurred(
                        "Failed to start advertising: WiFi Direct publisher is unavailable",
                        opId,
                        api: "WiFiDirectAdvertisementPublisher.Start",
                        result: "missing",
                        reason: reason);
                    return false;
                }

                if (_publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started)
                {
                    IsAdvertising = true;
                    SetDiscoverabilityStatus("Discoverable: Active", opId, reason);
                    RefreshOperationalState(opId, reason);
                    OnStatusChanged(
                        "WiFi Direct advertising is already active",
                        opId,
                        api: "WiFiDirectAdvertisementPublisher.Start",
                        result: "already_started",
                        reason: reason);
                    return true;
                }

                _publisher.Start();
                _lastPublisherStatus = _publisher.Status.ToString();

                if (_publisher.Status != WiFiDirectAdvertisementPublisherStatus.Started)
                {
                    IsAdvertising = false;
                    SetState(WiFiDirectServiceState.Error, opId, "WiFiDirectAdvertisementPublisher.Start", result: _publisher.Status.ToString(), reason: reason);
                    SetDiscoverabilityStatus("Discoverable: Failed - Mobile Hotspot may be enabled", opId, reason);
                    OnErrorOccurred(
                        $"Failed to start advertising: WiFi Direct publisher status is {_publisher.Status}",
                        opId,
                        api: "WiFiDirectAdvertisementPublisher.Start",
                        result: _publisher.Status.ToString(),
                        reason: reason);
                    return false;
                }

                IsAdvertising = true;
                SetDiscoverabilityStatus("Discoverable: Active", opId, reason);
                RefreshOperationalState(opId, reason, force: true);
                OnStatusChanged(
                    $"Advertising started. Device is discoverable as '{deviceName}'",
                    opId,
                    api: "WiFiDirectAdvertisementPublisher.Start",
                    result: "started",
                    reason: reason);
                return true;
            }
            catch (Exception ex)
            {
                IsAdvertising = false;
                SetState(WiFiDirectServiceState.Error, opId, "WiFiDirectAdvertisementPublisher.Start", result: ex.GetType().Name, reason: reason);
                SetDiscoverabilityStatus("Discoverable: Failed - Mobile Hotspot may be enabled", opId, reason);
                OnErrorOccurred(
                    $"Failed to start advertising: {ex.Message}",
                    opId,
                    api: "WiFiDirectAdvertisementPublisher.Start",
                    result: ex.GetType().Name,
                    reason: reason);
                return false;
            }
        }

        public void StopAdvertising()
        {
            StopAdvertising("legacy_stop_advertising");
        }

        public void StopAdvertising(string reason)
        {
            var opId = NextOperationId();
            CleanupAdvertisingResources(opId, reason);
            RefreshOperationalState(opId, reason);
            OnStatusChanged(
                "Advertising stopped. Device is no longer discoverable",
                opId,
                api: "WiFiDirectAdvertisementPublisher.Stop",
                result: "stopped",
                reason: reason);
        }

        public Task<bool> StartScanningAsync()
        {
            return StartScanAsync("legacy_start_scan");
        }

        public async Task<bool> StartScanAsync(string reason)
        {
            ThrowIfDisposed();

            var opId = NextOperationId();

            if (IsConnected || CurrentState is WiFiDirectServiceState.ConnectingOutbound or WiFiDirectServiceState.ConnectingInbound or WiFiDirectServiceState.IncomingPrompt)
            {
                OnErrorOccurred(
                    $"Cannot start scan while WiFi Direct state is {CurrentState}",
                    opId,
                    api: "DeviceInformation.CreateWatcher",
                    result: "rejected",
                    reason: reason);
                return false;
            }

            if (IsWatcherActive(_deviceWatcher))
            {
                IsScanning = true;
                RefreshOperationalState(opId, reason, force: true);
                OnStatusChanged(
                    "Scan is already running",
                    opId,
                    api: "DeviceWatcher.Start",
                    result: "already_started",
                    reason: reason);
                return true;
            }

            if (_deviceWatcher != null)
            {
                await StopWatcherAsync(clearDevices: false, reason: "restart_scan_wait_for_stopped", opId: opId).ConfigureAwait(false);
            }

            try
            {
                ClearDiscoveredDevices();
                OnStatusChanged(
                    "Starting WiFi Direct scan",
                    opId,
                    api: "WiFiDirectDevice.GetDeviceSelector",
                    result: "requested",
                    reason: reason);

                var deviceSelector = Windows.Devices.WiFiDirect.WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
                var watcher = DeviceInformation.CreateWatcher(deviceSelector);
                AttachWatcher(watcher);
                _deviceWatcher = watcher;
                _lastWatcherStatus = watcher.Status.ToString();
                OnStatusChanged(
                    "Device watcher created",
                    opId,
                    api: "DeviceInformation.CreateWatcher",
                    result: "created",
                    reason: reason);

                watcher.Start();
                _lastWatcherStatus = watcher.Status.ToString();
                IsScanning = true;
                RefreshOperationalState(opId, reason);
                OnStatusChanged(
                    "Scanning for WiFi Direct devices",
                    opId,
                    api: "DeviceWatcher.Start",
                    result: watcher.Status.ToString(),
                    reason: reason);
                return true;
            }
            catch (Exception ex)
            {
                await StopWatcherAsync(clearDevices: true, reason: "scan_start_failed", opId: opId).ConfigureAwait(false);
                SetState(WiFiDirectServiceState.Error, opId, "DeviceWatcher.Start", result: ex.GetType().Name, reason: reason);
                OnErrorOccurred(
                    $"Failed to start scanning: {ex.Message}",
                    opId,
                    api: "DeviceWatcher.Start",
                    result: ex.GetType().Name,
                    reason: reason);
                return false;
            }
        }

        public void StopScanning()
        {
            _ = StopScanAsync("legacy_stop_scan", clearDevices: true);
        }

        public Task StopScanAsync(string reason)
        {
            return StopScanAsync(reason, clearDevices: true);
        }

        public async Task StopScanAsync(string reason, bool clearDevices)
        {
            var opId = NextOperationId();
            await StopWatcherAsync(clearDevices, reason, opId).ConfigureAwait(false);
            OnStatusChanged(
                clearDevices ? "Scan stopped. Discovered device list cleared" : "Scan stopped",
                opId,
                api: "DeviceWatcher.Stop",
                result: "stopped",
                reason: reason);
        }

        public async Task<bool> ConnectToDeviceAsync(WiFiDirectDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            ThrowIfDisposed();

            var opId = NextOperationId();
            OnStatusChanged(
                $"Connect requested for {device.Name} ({device.Id})",
                opId,
                api: "WiFiDirectDevice.FromIdAsync",
                result: "requested",
                reason: "outbound_connect",
                peer: device);

            if (IsConnected || CurrentState is WiFiDirectServiceState.Connected or WiFiDirectServiceState.ConnectingOutbound or WiFiDirectServiceState.ConnectingInbound or WiFiDirectServiceState.IncomingPrompt)
            {
                OnErrorOccurred(
                    $"Cannot connect while WiFi Direct state is {CurrentState}",
                    opId,
                    api: "WiFiDirectDevice.FromIdAsync",
                    result: "rejected",
                    reason: "outbound_connect",
                    peer: device);
                return false;
            }

            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsConnected || CurrentState is WiFiDirectServiceState.Connected or WiFiDirectServiceState.ConnectingOutbound or WiFiDirectServiceState.ConnectingInbound or WiFiDirectServiceState.IncomingPrompt)
                {
                    OnErrorOccurred(
                        $"Cannot connect while WiFi Direct state is {CurrentState}",
                        opId,
                        api: "WiFiDirectDevice.FromIdAsync",
                        result: "rejected",
                        reason: "outbound_connect",
                        peer: device);
                    return false;
                }

                SetState(WiFiDirectServiceState.ConnectingOutbound, opId, "WiFiDirectDevice.FromIdAsync", result: "started", reason: "outbound_connect", peer: device);

                if (IsScanning)
                {
                    OnStatusChanged(
                        "Pausing scan before outbound connection",
                        opId,
                        api: "DeviceWatcher.Stop",
                        result: "requested",
                        reason: "scan_pause_before_connect",
                        peer: device);
                    await StopWatcherAsync(clearDevices: false, reason: "scan_pause_before_connect", opId: opId).ConfigureAwait(false);
                }

                OnStatusChanged(
                    $"Connecting to {device.Name} ({device.Id})",
                    opId,
                    api: "WiFiDirectDevice.FromIdAsync",
                    result: "started",
                    reason: "outbound_connect",
                    peer: device);

                var nativeDevice = await Windows.Devices.WiFiDirect.WiFiDirectDevice.FromIdAsync(device.Id);
                if (nativeDevice == null)
                {
                    RestorePostConnectionAttemptState(opId, "outbound_connect");
                    OnErrorOccurred(
                        $"Failed to create WiFi Direct device for {device.Name}",
                        opId,
                        api: "WiFiDirectDevice.FromIdAsync",
                        result: "null",
                        reason: "outbound_connect",
                        peer: device);
                    return false;
                }

                OnStatusChanged(
                    $"WiFi Direct device created for {device.Name}",
                    opId,
                    api: "WiFiDirectDevice.FromIdAsync",
                    result: "success",
                    reason: "outbound_connect",
                    peer: device);

                var endpoint = await WaitForEndpointReadyAsync(nativeDevice, opId, "outbound_connect", device).ConfigureAwait(false);
                if (endpoint == null)
                {
                    nativeDevice.Dispose();
                    RestorePostConnectionAttemptState(opId, "outbound_connect");
                    OnErrorOccurred(
                        $"WiFi Direct endpoint readiness timed out for {device.Name}",
                        opId,
                        api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                        result: "timeout",
                        reason: "outbound_connect",
                        peer: device);
                    return false;
                }

                ApplyConnectedDevice(device, nativeDevice, endpoint, opId, "outbound_connect");
                OnDeviceConnected(device);
                OnStatusChanged(
                    $"Connected to {device.Name}. {EndpointDiagnostics}",
                    opId,
                    api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                    result: "connected",
                    reason: "outbound_connect",
                    peer: device);
                return true;
            }
            catch (Exception ex)
            {
                RestorePostConnectionAttemptState(opId, "outbound_connect");
                OnErrorOccurred(
                    $"Connection error: {ex.Message}",
                    opId,
                    api: "WiFiDirectDevice.FromIdAsync",
                    result: ex.GetType().Name,
                    reason: "outbound_connect",
                    peer: device);
                return false;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public Task DisconnectAsync()
        {
            return DisconnectAsync("legacy_disconnect");
        }

        public async Task DisconnectAsync(string reason)
        {
            var opId = NextOperationId();
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_connectedDevice == null && _wifiDirectDevice == null)
                {
                    RefreshOperationalState(opId, reason);
                    return;
                }

                SetState(WiFiDirectServiceState.Disconnecting, opId, "WiFiDirectDevice.Dispose", result: "started", reason: reason, peer: _connectedDevice);

                var disconnectedDeviceName = _connectedDevice?.Name ?? "peer device";
                if (_connectedDevice != null)
                {
                    _connectedDevice.IsConnected = false;
                    _connectedDevice.Status = "Disconnected";
                }

                if (_wifiDirectDevice != null)
                {
                    _wifiDirectDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
                    _wifiDirectDevice.Dispose();
                    _wifiDirectDevice = null;
                }

                _connectedDevice = null;
                IsGroupOwner = false;
                LocalIP = null;
                RemoteIP = null;

                OnDeviceDisconnected();
                RefreshOperationalState(opId, reason, force: true);
                OnStatusChanged(
                    $"Disconnected from {disconnectedDeviceName}",
                    opId,
                    api: "WiFiDirectDevice.Dispose",
                    result: "complete",
                    reason: reason);
            }
            catch (Exception ex)
            {
                SetState(WiFiDirectServiceState.Error, opId, "WiFiDirectDevice.Dispose", result: ex.GetType().Name, reason: reason);
                OnErrorOccurred(
                    $"Disconnect error: {ex.Message}",
                    opId,
                    api: "WiFiDirectDevice.Dispose",
                    result: ex.GetType().Name,
                    reason: reason);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            var opId = NextOperationId();
            CleanupAdvertisingResources(opId, "service_dispose");
            _ = StopWatcherAsync(clearDevices: true, reason: "service_dispose", opId: opId);

            if (_wifiDirectDevice != null)
            {
                _wifiDirectDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _wifiDirectDevice.Dispose();
                _wifiDirectDevice = null;
            }

            _connectedDevice = null;
            IsGroupOwner = false;
            LocalIP = null;
            RemoteIP = null;
            _connectionLock.Dispose();
        }

        private async void OnOtherSideNotRunningApp(object? sender, EventArgs e)
        {
            OnStatusChanged(
                "Other side is not running the app. Disconnecting",
                NextOperationId(),
                api: "ConnectionService.OtherSideNotRunningApp",
                callback: "OtherSideNotRunningApp",
                result: "disconnecting",
                reason: "peer_protocol_missing");
            await DisconnectAsync("peer_protocol_missing").ConfigureAwait(false);
        }

        private void EnsurePublisher(string deviceName, string opId, string reason)
        {
            if (_publisher != null && _publisher.Status != WiFiDirectAdvertisementPublisherStatus.Aborted)
            {
                return;
            }

            if (_publisher != null)
            {
                _publisher.StatusChanged -= OnAdvertisementStatusChanged;
                try
                {
                    _publisher.Stop();
                }
                catch (Exception ex)
                {
                    LogDiagnostic(opId, "WiFiDirectAdvertisementPublisher.Stop", string.Empty, ex.GetType().Name, reason, message: ex.Message);
                }
            }

            _publisher = new WiFiDirectAdvertisementPublisher();
            _publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
            _publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                _publisher.Advertisement.LegacySettings.Ssid = deviceName;
            }

            _publisher.StatusChanged += OnAdvertisementStatusChanged;
            _lastPublisherStatus = _publisher.Status.ToString();
            OnStatusChanged(
                "WiFi Direct advertisement publisher created",
                opId,
                api: "WiFiDirectAdvertisementPublisher",
                result: "created",
                reason: reason);
        }

        private void EnsureConnectionListener(string opId, string reason)
        {
            if (_connectionListener != null)
            {
                return;
            }

            _connectionListener = new WiFiDirectConnectionListener();
            _connectionListener.ConnectionRequested += OnNativeConnectionRequested;
            OnStatusChanged(
                "WiFi Direct connection listener created",
                opId,
                api: "WiFiDirectConnectionListener",
                result: "created",
                reason: reason);
        }

        private void CleanupAdvertisingResources(string opId, string reason)
        {
            if (_publisher != null)
            {
                _publisher.StatusChanged -= OnAdvertisementStatusChanged;
                try
                {
                    if (_publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started)
                    {
                        _publisher.Stop();
                    }
                }
                catch (Exception ex)
                {
                    LogDiagnostic(opId, "WiFiDirectAdvertisementPublisher.Stop", string.Empty, ex.GetType().Name, reason, message: ex.Message);
                }

                _publisher = null;
            }

            if (_connectionListener != null)
            {
                _connectionListener.ConnectionRequested -= OnNativeConnectionRequested;
                _connectionListener = null;
                OnStatusChanged(
                    "WiFi Direct connection listener disposed",
                    opId,
                    api: "WiFiDirectConnectionListener",
                    result: "disposed",
                    reason: reason);
            }

            IsAdvertising = false;
            _lastPublisherStatus = "Stopped";
            SetDiscoverabilityStatus("Discoverable: Stopped", opId, reason);
        }

        private void AttachWatcher(DeviceWatcher watcher)
        {
            watcher.Added += OnDeviceAdded;
            watcher.Removed += OnDeviceRemoved;
            watcher.Updated += OnDeviceUpdated;
            watcher.EnumerationCompleted += OnEnumerationCompleted;
            watcher.Stopped += OnWatcherStopped;
        }

        private void DetachWatcher(DeviceWatcher watcher)
        {
            watcher.Added -= OnDeviceAdded;
            watcher.Removed -= OnDeviceRemoved;
            watcher.Updated -= OnDeviceUpdated;
            watcher.EnumerationCompleted -= OnEnumerationCompleted;
            watcher.Stopped -= OnWatcherStopped;
        }

        private async Task StopWatcherAsync(bool clearDevices, string reason, string opId)
        {
            var watcher = _deviceWatcher;
            if (watcher == null)
            {
                IsScanning = false;
                RefreshOperationalState(opId, reason);
                if (clearDevices)
                {
                    ClearDiscoveredDevices();
                }

                return;
            }

            try
            {
                _lastWatcherStatus = watcher.Status.ToString();
                OnStatusChanged(
                    "Stopping WiFi Direct watcher",
                    opId,
                    api: "DeviceWatcher.Stop",
                    result: "requested",
                    reason: reason);

                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    _watcherStoppedCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    watcher.Stop();
                    await AwaitWatcherStoppedAsync(opId, reason).ConfigureAwait(false);
                }
                else if (watcher.Status == DeviceWatcherStatus.Stopping)
                {
                    _watcherStoppedCompletionSource ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await AwaitWatcherStoppedAsync(opId, reason).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred(
                    $"Error stopping scan: {ex.Message}",
                    opId,
                    api: "DeviceWatcher.Stop",
                    result: ex.GetType().Name,
                    reason: reason);
            }
            finally
            {
                DetachWatcher(watcher);
                if (ReferenceEquals(_deviceWatcher, watcher))
                {
                    _deviceWatcher = null;
                }

                _watcherStoppedCompletionSource = null;
                IsScanning = false;
                _lastWatcherStatus = watcher.Status.ToString();

                if (clearDevices)
                {
                    ClearDiscoveredDevices();
                }

                RefreshOperationalState(opId, reason);
            }
        }

        private async Task AwaitWatcherStoppedAsync(string opId, string reason)
        {
            var stoppedTask = _watcherStoppedCompletionSource?.Task;
            if (stoppedTask == null)
            {
                return;
            }

            var completedTask = await Task.WhenAny(stoppedTask, Task.Delay(WatcherStopTimeout)).ConfigureAwait(false);
            if (completedTask != stoppedTask)
            {
                OnErrorOccurred(
                    "Timed out waiting for DeviceWatcher.Stopped",
                    opId,
                    api: "DeviceWatcher.Stopped",
                    callback: "Stopped",
                    result: "timeout",
                    reason: reason);
            }
        }

        private void ClearDiscoveredDevices()
        {
            _dispatcherQueue.TryEnqueue(() => DiscoveredDevices.Clear());
        }

        private async void OnNativeConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
        {
            var opId = NextOperationId();
            WiFiDirectConnectionRequest? request = null;
            WiFiDirectDevice? requestingDevice = null;

            try
            {
                request = args.GetConnectionRequest();
                var deviceInfo = request.DeviceInformation;
                requestingDevice = new WiFiDirectDevice
                {
                    Id = deviceInfo.Id,
                    Name = string.IsNullOrWhiteSpace(deviceInfo.Name) ? "Unknown Device" : deviceInfo.Name,
                    DeviceInfo = deviceInfo,
                    IsConnected = false,
                    Status = "Requesting Connection"
                };

                OnStatusChanged(
                    $"Connection request received from {requestingDevice.Name} ({requestingDevice.Id})",
                    opId,
                    api: "WiFiDirectConnectionListener.ConnectionRequested",
                    callback: "ConnectionRequested",
                    result: "received",
                    reason: "incoming_request",
                    peer: requestingDevice);

                if (IsConnected || CurrentState is WiFiDirectServiceState.ConnectingOutbound or WiFiDirectServiceState.ConnectingInbound or WiFiDirectServiceState.IncomingPrompt)
                {
                    OnStatusChanged(
                        $"Connection request rejected because WiFi Direct state is {CurrentState}",
                        opId,
                        api: "WiFiDirectConnectionListener.ConnectionRequested",
                        callback: "ConnectionRequested",
                        result: "rejected_busy",
                        reason: "incoming_request",
                        peer: requestingDevice);
                    return;
                }

                SetState(WiFiDirectServiceState.IncomingPrompt, opId, "WiFiDirectConnectionListener.ConnectionRequested", "prompting", "incoming_request", requestingDevice);

                var requestArgs = new ConnectionRequestEventArgs(requestingDevice);
                RaiseConnectionRequested(requestArgs, opId, requestingDevice);

                var completedTask = await Task.WhenAny(requestArgs.ResponseTask.Task, Task.Delay(IncomingRequestTimeout)).ConfigureAwait(false);
                var accepted = completedTask == requestArgs.ResponseTask.Task && await requestArgs.ResponseTask.Task.ConfigureAwait(false);

                if (completedTask != requestArgs.ResponseTask.Task)
                {
                    requestArgs.ResponseTask.TrySetResult(false);
                    OnStatusChanged(
                        $"Connection request from {requestingDevice.Name} timed out after {IncomingRequestTimeout.TotalSeconds:0} seconds",
                        opId,
                        api: "ConnectionRequestEventArgs.ResponseTask",
                        result: "timed_out",
                        reason: "incoming_request_timeout",
                        peer: requestingDevice);
                }

                if (!accepted)
                {
                    OnStatusChanged(
                        $"Connection request declined for {requestingDevice.Name}",
                        opId,
                        api: "ConnectionRequestEventArgs.ResponseTask",
                        result: completedTask == requestArgs.ResponseTask.Task ? "rejected" : "timed_out",
                        reason: "incoming_request",
                        peer: requestingDevice);
                    return;
                }

                OnStatusChanged(
                    $"Connection request accepted for {requestingDevice.Name}",
                    opId,
                    api: "ConnectionRequestEventArgs.ResponseTask",
                    result: "accepted",
                    reason: "incoming_request",
                    peer: requestingDevice);

                if (IsScanning)
                {
                    await StopWatcherAsync(clearDevices: false, reason: "incoming_accept_stop_scan", opId: opId).ConfigureAwait(false);
                }

                await ConnectInboundAsync(requestingDevice, opId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetState(WiFiDirectServiceState.Error, opId, "WiFiDirectConnectionListener.ConnectionRequested", ex.GetType().Name, "incoming_request", requestingDevice);
                OnErrorOccurred(
                    $"Error handling connection request: {ex.Message}",
                    opId,
                    api: "WiFiDirectConnectionListener.ConnectionRequested",
                    callback: "ConnectionRequested",
                    result: ex.GetType().Name,
                    reason: "incoming_request",
                    peer: requestingDevice);
            }
            finally
            {
                request?.Dispose();
                if (!IsConnected && CurrentState == WiFiDirectServiceState.IncomingPrompt)
                {
                RefreshOperationalState(opId, "incoming_request_complete", force: true);
                }
            }
        }

        private async Task ConnectInboundAsync(WiFiDirectDevice requestingDevice, string opId)
        {
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsConnected || CurrentState is WiFiDirectServiceState.Connected or WiFiDirectServiceState.ConnectingOutbound or WiFiDirectServiceState.ConnectingInbound)
                {
                    OnStatusChanged(
                        $"Accepted request rejected because WiFi Direct state is {CurrentState}",
                        opId,
                        api: "WiFiDirectDevice.FromIdAsync",
                        result: "rejected_busy",
                        reason: "incoming_connect",
                        peer: requestingDevice);
                    return;
                }

                SetState(WiFiDirectServiceState.ConnectingInbound, opId, "WiFiDirectDevice.FromIdAsync", result: "started", reason: "incoming_connect", peer: requestingDevice);

                var nativeDevice = await Windows.Devices.WiFiDirect.WiFiDirectDevice.FromIdAsync(requestingDevice.Id);
                if (nativeDevice == null)
                {
                    RestorePostConnectionAttemptState(opId, "incoming_connect");
                    OnErrorOccurred(
                        $"Failed to create WiFi Direct device for accepted request from {requestingDevice.Name}",
                        opId,
                        api: "WiFiDirectDevice.FromIdAsync",
                        result: "null",
                        reason: "incoming_connect",
                        peer: requestingDevice);
                    return;
                }

                OnStatusChanged(
                    $"WiFi Direct device created for accepted request from {requestingDevice.Name}",
                    opId,
                    api: "WiFiDirectDevice.FromIdAsync",
                    result: "success",
                    reason: "incoming_connect",
                    peer: requestingDevice);

                var endpoint = await WaitForEndpointReadyAsync(nativeDevice, opId, "incoming_connect", requestingDevice).ConfigureAwait(false);
                if (endpoint == null)
                {
                    nativeDevice.Dispose();
                    RestorePostConnectionAttemptState(opId, "incoming_connect");
                    OnErrorOccurred(
                        $"WiFi Direct endpoint readiness timed out for accepted request from {requestingDevice.Name}",
                        opId,
                        api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                        result: "timeout",
                        reason: "incoming_connect",
                        peer: requestingDevice);
                    return;
                }

                ApplyConnectedDevice(requestingDevice, nativeDevice, endpoint, opId, "incoming_connect");
                OnDeviceConnected(requestingDevice);
                OnStatusChanged(
                    $"Accepted connection from {requestingDevice.Name}. {EndpointDiagnostics}",
                    opId,
                    api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                    result: "connected",
                    reason: "incoming_connect",
                    peer: requestingDevice);
            }
            catch (Exception ex)
            {
                RestorePostConnectionAttemptState(opId, "incoming_connect");
                OnErrorOccurred(
                    $"Error creating WiFi Direct device: {ex.Message}",
                    opId,
                    api: "WiFiDirectDevice.FromIdAsync",
                    result: ex.GetType().Name,
                    reason: "incoming_connect",
                    peer: requestingDevice);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private void RaiseConnectionRequested(ConnectionRequestEventArgs args, string opId, WiFiDirectDevice peer)
        {
            if (ConnectionRequested == null)
            {
                OnStatusChanged(
                    "Connection request rejected because no app prompt handler is registered",
                    opId,
                    api: "ConnectionRequested",
                    result: "no_subscriber",
                    reason: "incoming_request",
                    peer: peer);
                args.ResponseTask.TrySetResult(false);
                return;
            }

            var enqueued = _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ConnectionRequested?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    args.ResponseTask.TrySetResult(false);
                    OnErrorOccurred(
                        $"Connection prompt handler failed: {ex.Message}",
                        opId,
                        api: "ConnectionRequested",
                        result: ex.GetType().Name,
                        reason: "incoming_request",
                        peer: peer);
                }
            });

            if (!enqueued)
            {
                args.ResponseTask.TrySetResult(false);
                OnErrorOccurred(
                    "Connection request rejected because the UI dispatcher is unavailable",
                    opId,
                    api: "DispatcherQueue.TryEnqueue",
                    result: "failed",
                    reason: "incoming_request",
                    peer: peer);
            }
        }

        private async Task<EndpointInfo?> WaitForEndpointReadyAsync(
            Windows.Devices.WiFiDirect.WiFiDirectDevice nativeDevice,
            string opId,
            string reason,
            WiFiDirectDevice peer)
        {
            OnStatusChanged(
                "Endpoint polling started",
                opId,
                api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                result: "started",
                reason: reason,
                peer: peer);

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < EndpointReadyTimeout)
            {
                try
                {
                    var endpoint = TryGetFirstIpv4Endpoint(nativeDevice);
                    if (endpoint != null)
                    {
                        OnStatusChanged(
                            $"Endpoint polling succeeded. Local: {endpoint.LocalIP}; Remote: {endpoint.RemoteIP}",
                            opId,
                            api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                            result: "success",
                            reason: reason,
                            peer: peer,
                            localIp: endpoint.LocalIP,
                            remoteIp: endpoint.RemoteIP);
                        return endpoint;
                    }
                }
                catch (Exception ex)
                {
                    OnStatusChanged(
                        $"Endpoint polling attempt failed: {ex.Message}",
                        opId,
                        api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                        result: ex.GetType().Name,
                        reason: reason,
                        peer: peer);
                }

                await Task.Delay(EndpointPollDelay).ConfigureAwait(false);
            }

            OnErrorOccurred(
                "Endpoint polling timed out before an IPv4 endpoint pair was available",
                opId,
                api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                result: "timeout",
                reason: reason,
                peer: peer);
            return null;
        }

        private static EndpointInfo? TryGetFirstIpv4Endpoint(Windows.Devices.WiFiDirect.WiFiDirectDevice nativeDevice)
        {
            foreach (var pair in nativeDevice.GetConnectionEndpointPairs())
            {
                var localIp = pair.LocalHostName?.ToString();
                var remoteIp = pair.RemoteHostName?.ToString();
                if (IsIpv4Address(localIp) && IsIpv4Address(remoteIp))
                {
                    return new EndpointInfo(localIp!, remoteIp!);
                }
            }

            return null;
        }

        private static bool IsIpv4Address(string? value)
        {
            return IPAddress.TryParse(value, out var address) &&
                   address.AddressFamily == AddressFamily.InterNetwork;
        }

        private void ApplyConnectedDevice(
            WiFiDirectDevice device,
            Windows.Devices.WiFiDirect.WiFiDirectDevice nativeDevice,
            EndpointInfo endpoint,
            string opId,
            string reason)
        {
            device.IsConnected = true;
            device.Status = "Connected";
            _connectedDevice = device;
            _wifiDirectDevice = nativeDevice;
            _wifiDirectDevice.ConnectionStatusChanged += OnConnectionStatusChanged;
            LocalIP = endpoint.LocalIP;
            RemoteIP = endpoint.RemoteIP;
            IsGroupOwner = DetermineGroupOwnerStatus(endpoint.LocalIP, endpoint.RemoteIP, opId, reason, device);
            SetState(WiFiDirectServiceState.Connected, opId, "WiFiDirectDevice.GetConnectionEndpointPairs", result: "connected", reason: reason, peer: device);
            OnStatusChanged(
                $"Role inferred from endpoint IPs: {RoleName}. {EndpointDiagnostics}",
                opId,
                api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                result: "role_inferred",
                reason: reason,
                peer: device,
                localIp: endpoint.LocalIP,
                remoteIp: endpoint.RemoteIP);
        }

        private bool DetermineGroupOwnerStatus(string localIp, string remoteIp, string opId, string reason, WiFiDirectDevice? peer)
        {
            if (!IPAddress.TryParse(localIp, out var localAddress) ||
                !IPAddress.TryParse(remoteIp, out var remoteAddress))
            {
                OnErrorOccurred(
                    $"Could not parse IP addresses - Local: {localIp}, Remote: {remoteIp}",
                    opId,
                    api: "IPAddress.TryParse",
                    result: "failed",
                    reason: reason,
                    peer: peer,
                    localIp: localIp,
                    remoteIp: remoteIp);
                return false;
            }

            if (localAddress.AddressFamily != AddressFamily.InterNetwork ||
                remoteAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                OnErrorOccurred(
                    $"Group owner inference expects IPv4 endpoints - Local: {localIp}, Remote: {remoteIp}",
                    opId,
                    api: "WiFiDirectDevice.GetConnectionEndpointPairs",
                    result: "not_ipv4",
                    reason: reason,
                    peer: peer,
                    localIp: localIp,
                    remoteIp: remoteIp);
                return false;
            }

            var localBytes = localAddress.GetAddressBytes();
            var remoteBytes = remoteAddress.GetAddressBytes();
            var sameSubnet = localBytes[0] == remoteBytes[0] &&
                             localBytes[1] == remoteBytes[1] &&
                             localBytes[2] == remoteBytes[2];

            return sameSubnet ? localBytes[3] < remoteBytes[3] : localBytes[3] == 1;
        }

        private void RestorePostConnectionAttemptState(string opId, string reason)
        {
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
            RefreshOperationalState(opId, reason, force: true);
        }

        private void OnConnectionStatusChanged(Windows.Devices.WiFiDirect.WiFiDirectDevice sender, object args)
        {
            var opId = NextOperationId();
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Connected)
                {
                    OnStatusChanged(
                        $"WiFi Direct connection status changed: {sender.ConnectionStatus}. {EndpointDiagnostics}",
                        opId,
                        api: "WiFiDirectDevice.ConnectionStatusChanged",
                        callback: "ConnectionStatusChanged",
                        result: sender.ConnectionStatus.ToString(),
                        reason: "native_status_changed",
                        peer: _connectedDevice);
                }
                else if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Disconnected)
                {
                    OnStatusChanged(
                        "WiFi Direct connection lost. Cleaning up session",
                        opId,
                        api: "WiFiDirectDevice.ConnectionStatusChanged",
                        callback: "ConnectionStatusChanged",
                        result: sender.ConnectionStatus.ToString(),
                        reason: "native_status_changed",
                        peer: _connectedDevice);
                    _ = DisconnectAsync("native_status_disconnected");
                }
            });
        }

        private void OnAdvertisementStatusChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
        {
            var opId = NextOperationId();
            _dispatcherQueue.TryEnqueue(() =>
            {
                _lastPublisherStatus = args.Status.ToString();
                switch (args.Status)
                {
                    case WiFiDirectAdvertisementPublisherStatus.Started:
                        IsAdvertising = true;
                        SetDiscoverabilityStatus("Discoverable: Active", opId, "publisher_status_changed");
                        RefreshOperationalState(opId, "publisher_status_changed");
                        OnStatusChanged(
                            "Advertisement status changed: Started",
                            opId,
                            api: "WiFiDirectAdvertisementPublisher.StatusChanged",
                            callback: "StatusChanged",
                            result: args.Status.ToString(),
                            reason: "publisher_status_changed");
                        break;
                    case WiFiDirectAdvertisementPublisherStatus.Aborted:
                        IsAdvertising = false;
                        SetDiscoverabilityStatus("Discoverable: Failed - Mobile Hotspot may be enabled", opId, "publisher_aborted");
                        SetState(WiFiDirectServiceState.Error, opId, "WiFiDirectAdvertisementPublisher.StatusChanged", args.Error.ToString(), "publisher_aborted");
                        OnErrorOccurred(
                            $"WiFi Direct advertising aborted: {args.Error}",
                            opId,
                            api: "WiFiDirectAdvertisementPublisher.StatusChanged",
                            callback: "StatusChanged",
                            result: args.Error.ToString(),
                            reason: "publisher_aborted");
                        break;
                    case WiFiDirectAdvertisementPublisherStatus.Stopped:
                        IsAdvertising = false;
                        SetDiscoverabilityStatus("Discoverable: Stopped", opId, "publisher_status_changed");
                        RefreshOperationalState(opId, "publisher_status_changed");
                        OnStatusChanged(
                            "Advertisement status changed: Stopped",
                            opId,
                            api: "WiFiDirectAdvertisementPublisher.StatusChanged",
                            callback: "StatusChanged",
                            result: args.Status.ToString(),
                            reason: "publisher_status_changed");
                        break;
                    default:
                        OnStatusChanged(
                            $"Advertisement status changed: {args.Status}",
                            opId,
                            api: "WiFiDirectAdvertisementPublisher.StatusChanged",
                            callback: "StatusChanged",
                            result: args.Status.ToString(),
                            reason: "publisher_status_changed");
                        break;
                }
            });
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation deviceInfo)
        {
            var opId = NextOperationId();
            _dispatcherQueue.TryEnqueue(() =>
            {
                var peerName = string.IsNullOrWhiteSpace(deviceInfo.Name) ? "Unknown Device" : deviceInfo.Name;
                var existingDevice = DiscoveredDevices.FirstOrDefault(d => d.Id == deviceInfo.Id);
                if (existingDevice != null)
                {
                    existingDevice.Name = peerName;
                    existingDevice.DeviceInfo = deviceInfo;
                    existingDevice.Status = "Available";
                    OnStatusChanged(
                        $"Discovered device refreshed: {existingDevice.Name}",
                        opId,
                        api: "DeviceWatcher.Added",
                        callback: "Added",
                        result: "updated",
                        reason: "scan_device_added",
                        peer: existingDevice);
                    return;
                }

                var device = new WiFiDirectDevice
                {
                    Id = deviceInfo.Id,
                    Name = peerName,
                    DeviceInfo = deviceInfo,
                    IsConnected = false,
                    Status = "Available"
                };

                DiscoveredDevices.Add(device);
                OnStatusChanged(
                    $"Discovered device: {device.Name} ({device.Id})",
                    opId,
                    api: "DeviceWatcher.Added",
                    callback: "Added",
                    result: "added",
                    reason: "scan_device_added",
                    peer: device);
                OnDeviceDiscovered(device);
            });
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            var opId = NextOperationId();
            _dispatcherQueue.TryEnqueue(() =>
            {
                var device = DiscoveredDevices.FirstOrDefault(d => d.Id == deviceInfoUpdate.Id);
                if (device == null)
                {
                    return;
                }

                DiscoveredDevices.Remove(device);
                OnStatusChanged(
                    $"Discovered device removed: {device.Name}",
                    opId,
                    api: "DeviceWatcher.Removed",
                    callback: "Removed",
                    result: "removed",
                    reason: "scan_device_removed",
                    peer: device);
            });
        }

        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            var opId = NextOperationId();
            _dispatcherQueue.TryEnqueue(() =>
            {
                var device = DiscoveredDevices.FirstOrDefault(d => d.Id == deviceInfoUpdate.Id);
                if (device == null)
                {
                    return;
                }

                device.Status = "Updated";
                OnStatusChanged(
                    $"Discovered device updated: {device.Name}",
                    opId,
                    api: "DeviceWatcher.Updated",
                    callback: "Updated",
                    result: "updated",
                    reason: "scan_device_updated",
                    peer: device);
            });
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            var opId = NextOperationId();
            _dispatcherQueue.TryEnqueue(() =>
            {
                _lastWatcherStatus = sender.Status.ToString();
                IsScanning = true;
                RefreshOperationalState(opId, "scan_enumeration_completed");
                OnStatusChanged(
                    $"Scan initial enumeration completed. Found {DiscoveredDevices.Count} device(s); watcher remains active",
                    opId,
                    api: "DeviceWatcher.EnumerationCompleted",
                    callback: "EnumerationCompleted",
                    result: sender.Status.ToString(),
                    reason: "scan_enumeration_completed");
            });
        }

        private void OnWatcherStopped(DeviceWatcher sender, object args)
        {
            var opId = NextOperationId();
            _dispatcherQueue.TryEnqueue(() =>
            {
                _lastWatcherStatus = sender.Status.ToString();
                IsScanning = false;
                if (ReferenceEquals(_deviceWatcher, sender))
                {
                    _deviceWatcher = null;
                }

                _watcherStoppedCompletionSource?.TrySetResult(true);
                RefreshOperationalState(opId, "watcher_stopped_callback");
                var status = sender.Status == DeviceWatcherStatus.Aborted ? "aborted" : "stopped";
                OnStatusChanged(
                    $"Device watcher {status}: {sender.Status}",
                    opId,
                    api: "DeviceWatcher.Stopped",
                    callback: "Stopped",
                    result: sender.Status.ToString(),
                    reason: status == "aborted" ? "watcher_aborted" : "watcher_stopped");
            });
        }

        private void SetDiscoverabilityStatus(string status, string opId, string reason)
        {
            DiscoverabilityStatus = status;
            LogDiagnostic(opId, "WiFiDirectAdvertisementPublisher", string.Empty, "discoverability_status", reason, message: status);
        }

        private void SetState(
            WiFiDirectServiceState state,
            string opId,
            string api,
            string result,
            string reason,
            WiFiDirectDevice? peer = null)
        {
            if (CurrentState == state)
            {
                return;
            }

            CurrentState = state;
            LogDiagnostic(opId, api, string.Empty, result, reason, peer, message: $"state={state}");
        }

        private void RefreshOperationalState(string opId, string reason, bool force = false)
        {
            if (!force &&
                CurrentState is WiFiDirectServiceState.ConnectingOutbound or
                WiFiDirectServiceState.ConnectingInbound or
                WiFiDirectServiceState.IncomingPrompt or
                WiFiDirectServiceState.Disconnecting)
            {
                return;
            }

            var state = IsConnected
                ? WiFiDirectServiceState.Connected
                : IsScanning
                    ? WiFiDirectServiceState.Scanning
                    : IsAdvertising
                        ? WiFiDirectServiceState.Advertising
                        : WiFiDirectServiceState.Idle;

            SetState(state, opId, "WiFiDirectService.RefreshOperationalState", state.ToString(), reason);
        }

        private void OnDeviceDiscovered(WiFiDirectDevice device)
        {
            DeviceDiscovered?.Invoke(this, device);
        }

        private void OnDeviceConnected(WiFiDirectDevice device)
        {
            DeviceConnected?.Invoke(this, device);
        }

        private void OnDeviceDisconnected()
        {
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }

        private void OnStatusChanged(
            string status,
            string opId,
            string api,
            string result,
            string reason,
            string callback = "",
            WiFiDirectDevice? peer = null,
            string? localIp = null,
            string? remoteIp = null)
        {
            LogDiagnostic(opId, api, callback, result, reason, peer, localIp, remoteIp, status);
            StatusChanged?.Invoke(this, status);
        }

        private void OnErrorOccurred(
            string error,
            string opId,
            string api,
            string result,
            string reason,
            string callback = "",
            WiFiDirectDevice? peer = null,
            string? localIp = null,
            string? remoteIp = null)
        {
            LogDiagnostic(opId, api, callback, result, reason, peer, localIp, remoteIp, error);
            ErrorOccurred?.Invoke(this, error);
        }

        private void LogDiagnostic(
            string opId,
            string api,
            string callback,
            string result,
            string reason,
            WiFiDirectDevice? peer = null,
            string? localIp = null,
            string? remoteIp = null,
            string? message = null)
        {
            var line = string.Join(" | ",
                DateTimeOffset.Now.ToString("O"),
                "platform=windows",
                $"opId={opId}",
                $"state={CurrentState}",
                $"api={ValueOrNone(api)}",
                $"callback={ValueOrNone(callback)}",
                $"result={ValueOrNone(result)}",
                $"reason={ValueOrNone(reason)}",
                $"peerId={ValueOrNone(peer?.Id)}",
                $"peerName={ValueOrNone(peer?.Name)}",
                $"watcherStatus={ValueOrNone(_deviceWatcher?.Status.ToString() ?? _lastWatcherStatus)}",
                $"publisherStatus={ValueOrNone(_publisher?.Status.ToString() ?? _lastPublisherStatus)}",
                $"localIp={ValueOrNone(localIp ?? LocalIP)}",
                $"remoteIp={ValueOrNone(remoteIp ?? RemoteIP)}",
                $"message={ValueOrNone(message)}");
            Debug.WriteLine(line);
        }

        private string NextOperationId()
        {
            return $"wfd-{Interlocked.Increment(ref _operationCounter):000000}";
        }

        private static string ValueOrNone(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value.Replace(Environment.NewLine, " ");
        }

        private static bool IsWatcherActive(DeviceWatcher? watcher)
        {
            return watcher?.Status is DeviceWatcherStatus.Started or
                DeviceWatcherStatus.EnumerationCompleted or
                DeviceWatcherStatus.Stopping;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }

        private sealed record EndpointInfo(string LocalIP, string RemoteIP);
    }
}
