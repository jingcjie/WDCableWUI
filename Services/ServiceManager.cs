using System;
using System.Threading.Tasks;

namespace WDCableWUI.Services
{
    /// <summary>
    /// Singleton service manager that provides global access to WiFiDirectService
    /// and its managed ConnectionService.
    /// </summary>
    public static class ServiceManager
    {
        private static WiFiDirectService? _wifiDirectService;
        private static ConnectionService? _connectionService;
        private static ChatService? _chatService;
        private static SpeedTestService? _speedTestService;
        private static FileTransferService? _fileTransferService;
        private static readonly object _lock = new object();
        private static bool _isInitialized = false;
        
        /// <summary>
        /// Gets the singleton WiFiDirectService instance.
        /// </summary>
        public static WiFiDirectService? WiFiDirectService
        {
            get
            {
                if (!_isInitialized)
                {
                    throw new InvalidOperationException("ServiceManager must be initialized before accessing services. Call Initialize() first.");
                }
                return _wifiDirectService;
            }
        }
        
        /// <summary>
        /// Gets the singleton ConnectionService instance.
        /// </summary>
        public static ConnectionService? ConnectionService
        {
            get
            {
                if (!_isInitialized)
                {
                    throw new InvalidOperationException("ServiceManager must be initialized before accessing services. Call Initialize() first.");
                }
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
                if (!_isInitialized)
                {
                    throw new InvalidOperationException("ServiceManager must be initialized before accessing services. Call Initialize() first.");
                }
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
                if (!_isInitialized)
                {
                    throw new InvalidOperationException("ServiceManager must be initialized before accessing services. Call Initialize() first.");
                }
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
                if (!_isInitialized)
                {
                    throw new InvalidOperationException("ServiceManager must be initialized before accessing services. Call Initialize() first.");
                }
                return _fileTransferService;
            }
        }
        
        /// <summary>
        /// Gets whether the ServiceManager has been initialized.
        /// </summary>
        public static bool IsInitialized => _isInitialized;
        
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
                
                try
                {
                    _wifiDirectService = new WiFiDirectService();
                    _connectionService = ConnectionService.Instance;
                    
                    // Initialize ConnectionService with WiFiDirectService
                    _connectionService.Initialize(_wifiDirectService);
                    
                    // Set up bidirectional event subscription
                    _wifiDirectService.SetConnectionService(_connectionService);
                    
                    // Set initialized flag before creating other services to avoid circular dependency
                    _isInitialized = true;
                    
                    // Initialize ChatService and SpeedTestService after WiFiDirectService
                    // These will be initialized when ConnectionService is established
                    InitializeAdditionalServices();
                }
                catch (System.Runtime.InteropServices.COMException comEx) when (comEx.HResult == unchecked((int)0x80070032) || comEx.HResult == unchecked((int)0x80004005))
                {
                    // WiFi Direct not supported (ERROR_NOT_SUPPORTED or E_FAIL)
                    throw new NotSupportedException("WiFi Direct is not supported on this system. The Windows version may be too low or no wireless card is installed correctly.", comEx);
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    // WiFi Direct access denied
                    throw new NotSupportedException("WiFi Direct access is denied. Please check if WiFi is enabled and the application has necessary permissions.", uaEx);
                }
                catch (Exception ex)
                {
                    // Check if the inner exception or message indicates WiFi Direct is not supported
                    if (ex.Message.Contains("WiFi Direct") || ex.Message.Contains("not supported") || ex.Message.Contains("wireless"))
                    {
                        throw new NotSupportedException("WiFi Direct is not supported on this system. The Windows version may be too low or no wireless card is installed correctly.", ex);
                    }
                    throw new InvalidOperationException($"Failed to initialize services: {ex.Message}", ex);
                }
            }
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
            
            // Create new instances - these will automatically subscribe to ConnectionService events
            _chatService = ChatService.Instance;
            _speedTestService = SpeedTestService.Instance;
            _fileTransferService = FileTransferService.Instance;
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
                    // Dispose ChatService, SpeedTestService, and FileTransferService first
                    _chatService?.Dispose();
                    _chatService = null;
                    
                    _speedTestService?.Dispose();
                    _speedTestService = null;
                    
                    _fileTransferService?.Dispose();
                    _fileTransferService = null;
                    
                    // Reset singleton instances
                    ChatService.ResetInstance();
                    SpeedTestService.ResetInstance();
                    FileTransferService.ResetInstance();
                    
                    // Dispose and reset ConnectionService
                    _connectionService = null;
                    ConnectionService.ResetInstance();
                    
                    // Then dispose WiFiDirectService
                    _wifiDirectService?.Dispose();
                    _wifiDirectService = null;
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