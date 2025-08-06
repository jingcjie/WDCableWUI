using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace WDCableWUI.Services
{
    public class ConnectionService
    {
        private static ConnectionService? _instance;
        private static readonly object _lock = new object();
        
        private WiFiDirectService? _wifiDirectService;
        private readonly DispatcherQueue _dispatcherQueue;
        
        // TCP connections
        private TcpListener? _chatServer;
        private TcpListener? _speedTestServer;
        private TcpListener? _fileServer;
        
        private TcpClient? _chatClient;
        private TcpClient? _speedTestClient;
        private TcpClient? _fileClient;
        
        // Connection status
        private bool _isInitialized;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _chatConnected;
        private bool _speedTestConnected;
        private bool _fileConnected;
        private readonly object _connectionLock = new object();
        
        // Port definitions
        private const int CHAT_PORT = 8888;
        private const int SPEED_TEST_PORT = 8889;
        private const int FILE_PORT = 8890;
        
        // Events
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? ConnectionsEstablished;
        public event EventHandler? OtherSideNotRunningApp;
        
        // Properties
        public bool IsInitialized => _isInitialized;
        public bool IsGroupOwner => _wifiDirectService?.IsGroupOwner ?? false;
        public bool IsConnected => _wifiDirectService?.IsConnected ?? false;
        public TcpClient? ChatConnection => _chatClient;
        public TcpClient? SpeedTestConnection => _speedTestClient;
        public TcpClient? FileConnection => _fileClient;
        
        /// <summary>
        /// Checks if all TCP connections (Chat, SpeedTest, and File) are healthy and connected.
        /// </summary>
        /// <returns>True if all connections are established and connected, false otherwise.</returns>
        public bool IsConnectionHealthy()
        {
            lock (_connectionLock)
            {
                // Check if all connections exist and are still connected
                bool chatHealthy = _chatClient != null && _chatClient.Connected;
                bool speedTestHealthy = _speedTestClient != null && _speedTestClient.Connected;
                bool fileHealthy = _fileClient != null && _fileClient.Connected;
                
                return chatHealthy && speedTestHealthy && fileHealthy;
            }
        }
        
        private ConnectionService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _cancellationTokenSource = new CancellationTokenSource();
        }
        
        public static ConnectionService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new ConnectionService();
                        }
                    }
                }
                return _instance;
            }
        }
        
        public static void ResetInstance()
        {
            lock (_lock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }
        
        public void Initialize(WiFiDirectService wifiDirectService)
        {
            if (_wifiDirectService != null)
            {
                // Unsubscribe from previous service
                _wifiDirectService.DeviceConnected -= OnWiFiDirectConnected;
                _wifiDirectService.DeviceDisconnected -= OnWiFiDirectDisconnected;
            }
            
            _wifiDirectService = wifiDirectService ?? throw new ArgumentNullException(nameof(wifiDirectService));
            
            // Subscribe to WiFi Direct events
            _wifiDirectService.DeviceConnected += OnWiFiDirectConnected;
            _wifiDirectService.DeviceDisconnected += OnWiFiDirectDisconnected;
        }
        
        private async void OnWiFiDirectConnected(object? sender, WiFiDirectDevice device)
        {
            try
            {
                OnStatusChanged("WiFi Direct connected. Initializing TCP connections...");
                await InitializeConnectionsAsync();
                
                // Wait 6 seconds and check if connection is healthy
                await Task.Delay(6000);
                if (!IsConnectionHealthy())
                {
                    OnStatusChanged("Connection health check failed - other side may not be running this app");
                    OnOtherSideNotRunningApp();
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to initialize connections: {ex.Message}");
            }
        }
        
        private void OnWiFiDirectDisconnected(object? sender, EventArgs e)
        {
            OnStatusChanged("WiFi Direct disconnected. Cleaning up connections...");
            CleanupConnections();
        }
        
        public async Task InitializeConnectionsAsync()
        {
            if (_isInitialized)
            {
                OnStatusChanged("Connections already initialized.");
                return;
            }
            
            if (_wifiDirectService == null || !_wifiDirectService.IsConnected)
            {
                throw new InvalidOperationException("WiFi Direct must be connected before initializing TCP connections.");
            }
            
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                
                if (_wifiDirectService?.IsGroupOwner == true)
                {
                    OnStatusChanged("Device is Group Owner. Starting TCP servers...");
                    await StartServersAsync();
                    // For Group Owner, ConnectionsEstablished will be fired when all clients connect
                }
                else
                {
                    OnStatusChanged("Device is Client. Connecting to TCP servers...");
                    await ConnectToServersAsync();
                    // For Client, all connections are established immediately
                    OnStatusChanged("All TCP connections established - Services are now available");
                    ConnectionsEstablished?.Invoke(this, EventArgs.Empty);
                }
                
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to initialize connections: {ex.Message}");
                CleanupConnections();
                throw;
            }
        }
        
        private Task StartServersAsync()
        {
            if (string.IsNullOrEmpty(_wifiDirectService?.LocalIP))
            {
                throw new InvalidOperationException("Local IP is not available");
            }
                
            var localEndPoint = new IPEndPoint(IPAddress.Parse(_wifiDirectService.LocalIP), 0);
            
            try
            {
                // Start Chat Server
                _chatServer = new TcpListener(localEndPoint.Address, CHAT_PORT);
                _chatServer.Start();
                OnStatusChanged($"Chat server started on {localEndPoint.Address}:{CHAT_PORT}");
                
                // Start Speed Test Server
                _speedTestServer = new TcpListener(localEndPoint.Address, SPEED_TEST_PORT);
                _speedTestServer.Start();
                OnStatusChanged($"Speed test server started on {localEndPoint.Address}:{SPEED_TEST_PORT}");
                
                // Start File Server
                _fileServer = new TcpListener(localEndPoint.Address, FILE_PORT);
                _fileServer.Start();
                OnStatusChanged($"File server started on {localEndPoint.Address}:{FILE_PORT}");
                
                // Accept connections asynchronously
                var token = _cancellationTokenSource?.Token ?? CancellationToken.None;
                _ = Task.Run(() => AcceptConnectionsAsync(_chatServer, "Chat", (client) => _chatClient = client), token);
                _ = Task.Run(() => AcceptConnectionsAsync(_speedTestServer, "SpeedTest", (client) => _speedTestClient = client), token);
                _ = Task.Run(() => AcceptConnectionsAsync(_fileServer, "File", (client) => _fileClient = client), token);
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                throw;
            }
        }
        
        private async Task AcceptConnectionsAsync(TcpListener server, string serviceName, Action<TcpClient> setClient)
        {
            try
            {
                while (!(_cancellationTokenSource?.Token.IsCancellationRequested ?? true))
                {
                    OnStatusChanged($"Waiting for {serviceName} client connection...");
                    var client = await server.AcceptTcpClientAsync();
                    
                    if (client != null)
                    {
                        setClient(client);
                        OnStatusChanged($"{serviceName} client connected from {client.Client.RemoteEndPoint}");
                        
                        // Mark this service as connected and check if all are ready
                        lock (_connectionLock)
                        {
                            if (serviceName == "Chat") _chatConnected = true;
                            else if (serviceName == "SpeedTest") _speedTestConnected = true;
                            else if (serviceName == "File") _fileConnected = true;
                            
                            if (_chatConnected && _speedTestConnected && _fileConnected)
                            {
                                OnStatusChanged("All TCP connections established - Services are now available");
                                ConnectionsEstablished?.Invoke(this, EventArgs.Empty);
                            }
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Server was stopped, this is expected
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error accepting {serviceName} connections: {ex.Message}");
            }
        }
        
        private async Task ConnectToServersAsync()
        {
            var remoteIP = _wifiDirectService?.RemoteIP;
            if (string.IsNullOrEmpty(remoteIP))
            {
                throw new InvalidOperationException("Remote IP is not available for connection");
            }
            
            var maxRetries = 3;
            var retryDelay = TimeSpan.FromSeconds(2);
            
            // Connect to Chat Server
            _chatClient = await ConnectWithRetryAsync(remoteIP, CHAT_PORT, "Chat", maxRetries, retryDelay);
            
            // Connect to Speed Test Server
            _speedTestClient = await ConnectWithRetryAsync(remoteIP, SPEED_TEST_PORT, "SpeedTest", maxRetries, retryDelay);
            
            // Connect to File Server
            _fileClient = await ConnectWithRetryAsync(remoteIP, FILE_PORT, "File", maxRetries, retryDelay);
            
        }
        
        private async Task<TcpClient?> ConnectWithRetryAsync(string remoteIP, int port, string serviceName, int maxRetries, TimeSpan retryDelay)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    OnStatusChanged($"Connecting to {serviceName} server at {remoteIP}:{port} (attempt {attempt}/{maxRetries})...");
                    
                    var client = new TcpClient();
                    await client.ConnectAsync(remoteIP, port);
                    
                    OnStatusChanged($"Successfully connected to {serviceName} server.");
                    return client;
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries)
                    {
                        OnStatusChanged($"Connection to {serviceName} failed, retrying in {retryDelay.TotalSeconds} seconds...");
                        await Task.Delay(retryDelay, _cancellationTokenSource?.Token ?? CancellationToken.None);
                    }
                    else
                    {
                        throw new Exception($"Failed to connect to {serviceName} server after {maxRetries} attempts: {ex.Message}");
                    }
                }
            }
            
            return null; // Should never reach here
        }
        
        public void CleanupConnections()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                
                // Close client connections
                _chatClient?.Close();
                _speedTestClient?.Close();
                _fileClient?.Close();
                
                _chatClient = null;
                _speedTestClient = null;
                _fileClient = null;
                
                // Reset connection tracking
                lock (_connectionLock)
                {
                    _chatConnected = false;
                    _speedTestConnected = false;
                    _fileConnected = false;
                }
                
                // Stop servers
                _chatServer?.Stop();
                _speedTestServer?.Stop();
                _fileServer?.Stop();
                
                _chatServer = null;
                _speedTestServer = null;
                _fileServer = null;
                
                _isInitialized = false;
                OnStatusChanged("All connections cleaned up.");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error during cleanup: {ex.Message}");
            }
        }
        
        public void Dispose()
        {
            CleanupConnections();
            _cancellationTokenSource?.Dispose();
            
            if (_wifiDirectService != null)
            {
                _wifiDirectService.DeviceConnected -= OnWiFiDirectConnected;
                _wifiDirectService.DeviceDisconnected -= OnWiFiDirectDisconnected;
            }
        }
        
        private void OnStatusChanged(string status)
        {
            _dispatcherQueue?.TryEnqueue(() => StatusChanged?.Invoke(this, status));
        }
        
        private void OnErrorOccurred(string error)
        {
            _dispatcherQueue?.TryEnqueue(() => ErrorOccurred?.Invoke(this, error));
        }
        
        private void OnOtherSideNotRunningApp()
        {
            _dispatcherQueue?.TryEnqueue(() => OtherSideNotRunningApp?.Invoke(this, EventArgs.Empty));
        }
    }
}