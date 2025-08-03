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
        /// Gets the current ConnectionService instance managed by WiFiDirectService.
        /// Returns null if no WiFi Direct connection is established.
        /// </summary>
        public static ConnectionService? ConnectionService => _wifiDirectService?.ConnectionService;
        
        /// <summary>
        /// Gets the singleton ChatService instance.
        /// </summary>
        public static ChatService ChatService => ChatService.Instance;
        
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
                    _isInitialized = true;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to initialize WiFiDirectService: {ex.Message}", ex);
                }
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
                    // Dispose ChatService first
                    ChatService.ResetInstance();
                    
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