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
        private readonly WiFiDirectService _wifiDirectService;
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
        
        // Properties
        public bool IsInitialized => _isInitialized;
        public bool IsGroupOwner => _wifiDirectService?.IsGroupOwner ?? false;
        public TcpClient? ChatConnection => _chatClient;
        public TcpClient? SpeedTestConnection => _speedTestClient;
        public TcpClient? FileConnection => _fileClient;
        
        public ConnectionService(WiFiDirectService wifiDirectService)
        {
            _wifiDirectService = wifiDirectService ?? throw new ArgumentNullException(nameof(wifiDirectService));
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _cancellationTokenSource = new CancellationTokenSource();
            
            // Subscribe to WiFi Direct events
            _wifiDirectService.DeviceConnected += OnWiFiDirectConnected;
            _wifiDirectService.DeviceDisconnected += OnWiFiDirectDisconnected;
        }
        
        private async void OnWiFiDirectConnected(object? sender, WiFiDirectDevice device)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[ConnectionService] OnWiFiDirectConnected called");
                OnStatusChanged("WiFi Direct connected. Initializing TCP connections...");
                await InitializeConnectionsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] OnWiFiDirectConnected exception: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[ConnectionService] InitializeConnectionsAsync called. IsInitialized: {_isInitialized}");
            
            if (_isInitialized)
            {
                System.Diagnostics.Debug.WriteLine("[ConnectionService] Already initialized, returning");
                OnStatusChanged("Connections already initialized.");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine($"[ConnectionService] WiFi Direct IsConnected: {_wifiDirectService.IsConnected}");
            if (!_wifiDirectService.IsConnected)
            {
                System.Diagnostics.Debug.WriteLine("[ConnectionService] WiFi Direct not connected, throwing exception");
                throw new InvalidOperationException("WiFi Direct must be connected before initializing TCP connections.");
            }
            
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] IsGroupOwner: {_wifiDirectService.IsGroupOwner}");
                if (_wifiDirectService.IsGroupOwner)
                {
                    System.Diagnostics.Debug.WriteLine("[ConnectionService] Starting TCP servers as Group Owner");
                    OnStatusChanged("Device is Group Owner. Starting TCP servers...");
                    await StartServersAsync();
                    // For Group Owner, ConnectionsEstablished will be fired when all clients connect
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[ConnectionService] Connecting to TCP servers as Client");
                    OnStatusChanged("Device is Client. Connecting to TCP servers...");
                    await ConnectToServersAsync();
                    // For Client, all connections are established immediately
                    System.Diagnostics.Debug.WriteLine("[ConnectionService] All client connections established, firing ConnectionsEstablished event");
                    OnStatusChanged("All TCP connections established - Services are now available");
                    ConnectionsEstablished?.Invoke(this, EventArgs.Empty);
                }
                
                _isInitialized = true;
                System.Diagnostics.Debug.WriteLine("[ConnectionService] InitializeConnectionsAsync completed successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] InitializeConnectionsAsync exception: {ex.Message}");
                OnErrorOccurred($"Failed to initialize connections: {ex.Message}");
                CleanupConnections();
                throw;
            }
        }
        
        private Task StartServersAsync()
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionService] StartServersAsync called. LocalIP: {_wifiDirectService.LocalIP}");
            
            if (string.IsNullOrEmpty(_wifiDirectService.LocalIP))
            {
                System.Diagnostics.Debug.WriteLine("[ConnectionService] Local IP is null or empty, throwing exception");
                throw new InvalidOperationException("Local IP is not available");
            }
                
            var localEndPoint = new IPEndPoint(IPAddress.Parse(_wifiDirectService.LocalIP), 0);
            System.Diagnostics.Debug.WriteLine($"[ConnectionService] Local endpoint created: {localEndPoint.Address}");
            
            try
            {
                // Start Chat Server
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] Starting Chat server on {localEndPoint.Address}:{CHAT_PORT}");
                _chatServer = new TcpListener(localEndPoint.Address, CHAT_PORT);
                _chatServer.Start();
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] Chat server started successfully");
                OnStatusChanged($"Chat server started on {localEndPoint.Address}:{CHAT_PORT}");
                
                // Start Speed Test Server
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] Starting SpeedTest server on {localEndPoint.Address}:{SPEED_TEST_PORT}");
                _speedTestServer = new TcpListener(localEndPoint.Address, SPEED_TEST_PORT);
                _speedTestServer.Start();
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] SpeedTest server started successfully");
                OnStatusChanged($"Speed test server started on {localEndPoint.Address}:{SPEED_TEST_PORT}");
                
                // Start File Server
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] Starting File server on {localEndPoint.Address}:{FILE_PORT}");
                _fileServer = new TcpListener(localEndPoint.Address, FILE_PORT);
                _fileServer.Start();
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] File server started successfully");
                OnStatusChanged($"File server started on {localEndPoint.Address}:{FILE_PORT}");
                
                // Accept connections asynchronously
                var token = _cancellationTokenSource?.Token ?? CancellationToken.None;
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] Starting async connection acceptance tasks");
                _ = Task.Run(() => AcceptConnectionsAsync(_chatServer, "Chat", (client) => _chatClient = client), token);
                _ = Task.Run(() => AcceptConnectionsAsync(_speedTestServer, "SpeedTest", (client) => _speedTestClient = client), token);
                _ = Task.Run(() => AcceptConnectionsAsync(_fileServer, "File", (client) => _fileClient = client), token);
                
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] All servers started and async tasks launched");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] StartServersAsync exception: {ex.Message}");
                throw;
            }
        }
        
        private async Task AcceptConnectionsAsync(TcpListener server, string serviceName, Action<TcpClient> setClient)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] AcceptConnectionsAsync started for {serviceName}");
                while (!(_cancellationTokenSource?.Token.IsCancellationRequested ?? true))
                {
                    System.Diagnostics.Debug.WriteLine($"[ConnectionService] Waiting for {serviceName} client connection...");
                    OnStatusChanged($"Waiting for {serviceName} client connection...");
                    var client = await server.AcceptTcpClientAsync();
                    
                    if (client != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ConnectionService] {serviceName} client connected from {client.Client.RemoteEndPoint}");
                        setClient(client);
                        OnStatusChanged($"{serviceName} client connected from {client.Client.RemoteEndPoint}");
                        
                        // Mark this service as connected and check if all are ready
                        lock (_connectionLock)
                        {
                            if (serviceName == "Chat") _chatConnected = true;
                            else if (serviceName == "SpeedTest") _speedTestConnected = true;
                            else if (serviceName == "File") _fileConnected = true;
                            
                            System.Diagnostics.Debug.WriteLine($"[ConnectionService] Connection status - Chat: {_chatConnected}, SpeedTest: {_speedTestConnected}, File: {_fileConnected}");
                            
                            if (_chatConnected && _speedTestConnected && _fileConnected)
                            {
                                System.Diagnostics.Debug.WriteLine("[ConnectionService] All connections established, firing ConnectionsEstablished event");
                                OnStatusChanged("All TCP connections established - Services are now available");
                                ConnectionsEstablished?.Invoke(this, EventArgs.Empty);
                            }
                        }
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] AcceptConnectionsAsync loop ended for {serviceName} (cancellation requested)");
            }
            catch (ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] AcceptConnectionsAsync for {serviceName}: Server was disposed (expected)");
                // Server was stopped, this is expected
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionService] AcceptConnectionsAsync for {serviceName} exception: {ex.Message}");
                OnErrorOccurred($"Error accepting {serviceName} connections: {ex.Message}");
            }
        }
        
        private async Task ConnectToServersAsync()
        {
            var remoteIP = _wifiDirectService.RemoteIP;
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
                    OnStatusChanged($"Failed to connect to {serviceName} server (attempt {attempt}/{maxRetries}): {ex.Message}");
                    
                    if (attempt < maxRetries)
                    {
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
    }
}