using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace WDCableWUI.Services
{
    /// <summary>
    /// Singleton service manager that provides global access to WiFiDirectService,
    /// SessionManager, and feature services.
    /// </summary>
    public static class ServiceManager
    {
        private const string DefaultWiFiDirectUnavailableMessage = "This WiFi adapter/driver does not support Windows WiFi Direct peer discovery required by WDCable.";

        private static DataManager? _dataManager;
        private static WiFiDirectService? _wifiDirectService;
        private static SessionManager? _sessionManager;
        private static ConnectionService? _connectionService;
        private static ChatService? _chatService;
        private static SpeedTestService? _speedTestService;
        private static FileTransferService? _fileTransferService;
        private static AudioService? _audioService;
        private static readonly object _lock = new object();
        private static bool _isInitialized = false;
        private static bool _areWiFiDirectServicesAvailable = false;
        private static string? _serviceInitializationError;
        
        /// <summary>
        /// Gets the singleton DataManager instance.
        /// </summary>
        public static DataManager? DataManager
        {
            get
            {
                if (_dataManager == null)
                {
                    try
                    {
                        _dataManager = DataManager.Instance;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"DataManager is not available: {ex.Message}");
                    }
                }

                return _dataManager;
            }
        }
        
        /// <summary>
        /// Gets the singleton WiFiDirectService instance.
        /// </summary>
        public static WiFiDirectService? WiFiDirectService
        {
            get
            {
                return _wifiDirectService;
            }
        }
        
        /// <summary>
        /// Gets the singleton SessionManager instance.
        /// </summary>
        public static SessionManager? SessionManager
        {
            get
            {
                return _sessionManager;
            }
        }

        /// <summary>
        /// Gets the singleton ConnectionService instance.
        /// </summary>
        public static ConnectionService? ConnectionService
        {
            get
            {
                return _connectionService;
            }
        }
        
        /// <summary>
        /// Gets the ChatService instance.
        /// </summary>
        public static ChatService? ChatService
        {
            get
            {
                return _chatService;
            }
        }
        
        /// <summary>
        /// Gets the SpeedTestService instance.
        /// </summary>
        public static SpeedTestService? SpeedTestService
        {
            get
            {
                return _speedTestService;
            }
        }
        
        /// <summary>
        /// Gets the FileTransferService instance.
        /// </summary>
        public static FileTransferService? FileTransferService
        {
            get
            {
                return _fileTransferService;
            }
        }

        /// <summary>
        /// Gets the AudioService instance.
        /// </summary>
        public static AudioService? AudioService
        {
            get
            {
                return _audioService;
            }
        }
        
        /// <summary>
        /// Gets whether the ServiceManager has been initialized.
        /// </summary>
        public static bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets whether WiFi Direct dependent services were initialized successfully.
        /// </summary>
        public static bool AreWiFiDirectServicesAvailable => _areWiFiDirectServicesAvailable;

        /// <summary>
        /// Gets the last WiFi Direct service initialization error, if any.
        /// </summary>
        public static string? ServiceInitializationError => _serviceInitializationError;

        /// <summary>
        /// Gets a user-facing message for pages that depend on WiFi Direct services.
        /// </summary>
        public static string ServiceUnavailableMessage => _serviceInitializationError ?? GetLocalizedString("WiFiDirectUnavailable_PeerDiscoveryMessage", DefaultWiFiDirectUnavailableMessage);
        
        /// <summary>
        /// Initializes the ServiceManager with a singleton WiFiDirectService instance.
        /// This should be called once during application startup.
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                if (_isInitialized)
                {
                    return; // Already initialized
                }

                _serviceInitializationError = null;
                _areWiFiDirectServicesAvailable = false;

                // Initialize DataManager independently so Settings and persisted app data still
                // work when WiFi Direct services cannot be created.
                try
                {
                    _dataManager = DataManager.Instance;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to initialize DataManager: {ex.Message}");
                    _serviceInitializationError = $"App settings storage could not be initialized: {ex.Message}";
                }

                _isInitialized = true;

                try
                {
                    ValidateWiFiDirectSupport();

                    _wifiDirectService = new WiFiDirectService();
                    _sessionManager = WDCableWUI.Services.SessionManager.Instance;
                    _sessionManager.Initialize(_wifiDirectService);
                    _connectionService = ConnectionService.Instance;

                    // ConnectionService is now a compatibility facade over SessionManager.
                    _connectionService.Initialize(_wifiDirectService, _sessionManager);

                    // Set up bidirectional event subscription
                    _wifiDirectService.SetConnectionService(_connectionService);

                    // Initialize ChatService and SpeedTestService after WiFiDirectService
                    // These will be initialized when ConnectionService is established
                    InitializeAdditionalServices();
                    _areWiFiDirectServicesAvailable = true;
                }
                catch (System.Runtime.InteropServices.COMException comEx) when (comEx.HResult == unchecked((int)0x80070032) || comEx.HResult == unchecked((int)0x80004005))
                {
                    // WiFi Direct not supported (ERROR_NOT_SUPPORTED or E_FAIL)
                    HandleWiFiDirectInitializationFailure("WiFi Direct is not supported on this system. Check that Windows, the wireless adapter, and WiFi Direct drivers support WiFi Direct.", comEx);
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    // WiFi Direct access denied
                    HandleWiFiDirectInitializationFailure("WiFi Direct access is denied. Please check that WiFi is enabled and the app has the required permissions.", uaEx);
                }
                catch (NotSupportedException nsEx)
                {
                    HandleWiFiDirectInitializationFailure(nsEx.Message, nsEx);
                }
                catch (Exception ex)
                {
                    // Check if the inner exception or message indicates WiFi Direct is not supported
                    if (ex.Message.Contains("WiFi Direct") || ex.Message.Contains("not supported") || ex.Message.Contains("wireless"))
                    {
                        HandleWiFiDirectInitializationFailure("WiFi Direct is not supported on this system. Check that Windows, the wireless adapter, and WiFi Direct drivers support WiFi Direct.", ex);
                    }
                    else
                    {
                        HandleWiFiDirectInitializationFailure($"Failed to initialize WiFi Direct services: {ex.Message}", ex);
                    }
                }
            }
        }

        private static void ValidateWiFiDirectSupport()
        {
            // Selector creation catches machines where the WinRT WiFi Direct API itself is
            // unavailable. The netsh capability probe catches adapters whose driver exposes
            // only partial WiFi Direct support, such as no P2P device discovery.
            _ = Windows.Devices.WiFiDirect.WiFiDirectDevice.GetDeviceSelector(
                Windows.Devices.WiFiDirect.WiFiDirectDeviceSelectorType.AssociationEndpoint);

            if (!HasWirelessAdapter())
            {
                throw new NotSupportedException("WiFi Direct is not supported on this system because no WiFi adapter was found. Check that Windows, the wireless adapter, and WiFi Direct drivers support WiFi Direct.");
            }

            var capabilityReport = TryReadWirelessCapabilityReport();
            if (capabilityReport == null)
            {
                return;
            }

            var missingCapabilities = capabilityReport.GetMissingRequiredCapabilities();
            if (missingCapabilities.Count == 0)
            {
                return;
            }

            throw new NotSupportedException(BuildUnsupportedCapabilityMessage(capabilityReport, missingCapabilities));
        }

        private static bool HasWirelessAdapter()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }

        private static WiFiDirectCapabilityReport? TryReadWirelessCapabilityReport()
        {
            try
            {
                var output = RunNetsh("wlan", "show", "wirelesscapabilities");
                if (string.IsNullOrWhiteSpace(output))
                {
                    return null;
                }

                var report = WiFiDirectCapabilityReport.Parse(output);
                var hasKnownCapabilityLabels = WiFiDirectCapabilityReport.RequiredCapabilities
                    .Any(capability => report.GetCapabilityValue(capability) != null);

                if (!hasKnownCapabilityLabels)
                {
                    Debug.WriteLine("WiFi Direct capability probe did not contain expected labels; skipping strict capability validation.");
                    return null;
                }

                return report;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WiFi Direct capability probe failed: {ex}");
                return null;
            }
        }

        private static string RunNetsh(params string[] arguments)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start netsh.exe.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException("Timed out while reading wireless capabilities.");
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"netsh exited with code {process.ExitCode}: {error}");
            }

            return output;
        }

        private static string BuildUnsupportedCapabilityMessage(
            WiFiDirectCapabilityReport report,
            IReadOnlyList<string> missingCapabilities)
        {
            var interfaceName = string.IsNullOrWhiteSpace(report.InterfaceName)
                ? string.Empty
                : $" Interface: {report.InterfaceName}.";

            Debug.WriteLine("WiFi Direct capability check failed." +
                            interfaceName +
                            $" Missing capability: {string.Join(", ", missingCapabilities)}." +
                            $" Reported capabilities: {report.BuildRequiredCapabilitySummary()}.");

            return GetLocalizedString("WiFiDirectUnavailable_PeerDiscoveryMessage", DefaultWiFiDirectUnavailableMessage);
        }

        private static string GetLocalizedString(string resourceKey, string fallback)
        {
            try
            {
                var value = ResourceLoader.GetForViewIndependentUse().GetString(resourceKey);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        private static void HandleWiFiDirectInitializationFailure(string message, Exception exception)
        {
            _areWiFiDirectServicesAvailable = false;
            _serviceInitializationError = message;
            System.Diagnostics.Debug.WriteLine($"{message} {exception}");
            DisposeWiFiDirectServices();
        }

        /// <summary>
        /// Initializes ChatService, SpeedTestService, and FileTransferService.
        /// Called after WiFiDirectService is initialized.
        /// </summary>
        private static void InitializeAdditionalServices()
        {
            // Reset any existing instances
            ChatService.ResetInstance();
            SpeedTestService.ResetInstance();
            FileTransferService.ResetInstance();
            WDCableWUI.Services.AudioService.ResetInstance();

            // Create new instances - these will automatically subscribe to ConnectionService events
            _chatService = ChatService.Instance;
            _speedTestService = SpeedTestService.Instance;
            _fileTransferService = FileTransferService.Instance;
            _audioService = WDCableWUI.Services.AudioService.Instance;
        }

        private static void DisposeWiFiDirectServices()
        {
            try
            {
                _chatService?.Dispose();
                _chatService = null;

                _speedTestService?.Dispose();
                _speedTestService = null;

                _fileTransferService?.Dispose();
                _fileTransferService = null;

                _audioService?.Dispose();
                _audioService = null;

                ChatService.ResetInstance();
                SpeedTestService.ResetInstance();
                FileTransferService.ResetInstance();
                WDCableWUI.Services.AudioService.ResetInstance();

                _connectionService = null;
                ConnectionService.ResetInstance();

                _sessionManager = null;
                SessionManager.ResetInstance();

                _wifiDirectService?.Dispose();
                _wifiDirectService = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disposing WiFi Direct services: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Shuts down the ServiceManager and disposes of all services.
        /// This should be called during application shutdown.
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                if (!_isInitialized)
                {
                    return; // Already shut down or never initialized
                }
                
                try
                {
                    DisposeWiFiDirectServices();
                    
                    // Finally dispose DataManager
                    _dataManager = null;
                    DataManager.ResetInstance();
                    
                    _serviceInitializationError = null;
                    _areWiFiDirectServicesAvailable = false;
                    _isInitialized = false;
                }
                catch (Exception ex)
                {
                    // Log error but don't throw during shutdown
                    System.Diagnostics.Debug.WriteLine($"Error during ServiceManager shutdown: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Gets whether a WiFi Direct connection is currently established.
        /// </summary>
        public static bool IsConnected => _wifiDirectService?.IsConnected ?? false;
        
        /// <summary>
        /// Gets whether the device is currently advertising (discoverable).
        /// </summary>
        public static bool IsAdvertising => _wifiDirectService?.IsAdvertising ?? false;
        
        /// <summary>
        /// Gets whether the device is currently scanning for other devices.
        /// </summary>
        public static bool IsScanning => _wifiDirectService?.IsScanning ?? false;
    }
}
