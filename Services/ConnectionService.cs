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
        private TcpListener _chatServer;
        private TcpListener _speedTestServer;
        private TcpListener _fileServer;
        
        private TcpClient _chatClient;
        private TcpClient _speedTestClient;
        private TcpClient _fileClient;
        
        // Connection status
        private bool _isInitialized;
        private CancellationTokenSource _cancellationTokenSource;
        
        // Port definitions
        private const int CHAT_PORT = 8888;
        private const int SPEED_TEST_PORT = 8889;
        private const int FILE_PORT = 8890;
        
        // Events
        public event EventHandler<string> StatusChanged;
        public event EventHandler<string> ErrorOccurred;
        public event EventHandler ConnectionsEstablished;
        
        // Properties
        public bool IsInitialized => _isInitialized;
        public bool IsGroupOwner => _wifiDirectService?.IsGroupOwner ?? false;
        public TcpClient ChatConnection => _chatClient;
        public TcpClient SpeedTestConnection => _speedTestClient;
        public TcpClient FileConnection => _fileClient;
        
        public ConnectionService(WiFiDirectService wifiDirectService)
        {
            _wifiDirectService = wifiDirectService ?? throw new ArgumentNullException(nameof(wifiDirectService));
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _cancellationTokenSource = new CancellationTokenSource();
            
            // Subscribe to WiFi Direct events
            _wifiDirectService.DeviceConnected += OnWiFiDirectConnected;
            _wifiDirectService.DeviceDisconnected += OnWiFiDirectDisconnected;
        }
        
        private async void OnWiFiDirectConnected(object sender, WiFiDirectDevice device)
        {
            try
            {
                OnStatusChanged("WiFi Direct connected. Initializing TCP connections...");
                await InitializeConnectionsAsync();
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to initialize connections: {ex.Message}");
            }
        }
        
        private void OnWiFiDirectDisconnected(object sender, EventArgs e)
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
            
            if (!_wifiDirectService.IsConnected)
            {
                throw new InvalidOperationException("WiFi Direct must be connected before initializing TCP connections.");
            }
            
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                
                if (_wifiDirectService.IsGroupOwner)
                {
                    OnStatusChanged("Device is Group Owner. Starting TCP servers...");
                    await StartServersAsync();
                }
                else
                {
                    OnStatusChanged("Device is Client. Connecting to TCP servers...");
                    await ConnectToServersAsync();
                }
                
                _isInitialized = true;
                OnStatusChanged("TCP connections established successfully.");
                ConnectionsEstablished?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to initialize connections: {ex.Message}");
                CleanupConnections();
                throw;
            }
        }
        
        private async Task StartServersAsync()
        {
            var localEndPoint = new IPEndPoint(IPAddress.Parse(_wifiDirectService.LocalIP), 0);
            
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
            _ = Task.Run(() => AcceptConnectionsAsync(_chatServer, "Chat", (client) => _chatClient = client), _cancellationTokenSource.Token);
            _ = Task.Run(() => AcceptConnectionsAsync(_speedTestServer, "SpeedTest", (client) => _speedTestClient = client), _cancellationTokenSource.Token);
            _ = Task.Run(() => AcceptConnectionsAsync(_fileServer, "File", (client) => _fileClient = client), _cancellationTokenSource.Token);
        }
        
        private async Task AcceptConnectionsAsync(TcpListener server, string serviceName, Action<TcpClient> setClient)
        {
            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    OnStatusChanged($"Waiting for {serviceName} client connection...");
                    var client = await server.AcceptTcpClientAsync();
                    
                    if (client != null)
                    {
                        setClient(client);
                        OnStatusChanged($"{serviceName} client connected from {client.Client.RemoteEndPoint}");
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
            var remoteIP = _wifiDirectService.RemoteIP;
            var maxRetries = 3;
            var retryDelay = TimeSpan.FromSeconds(2);
            
            // Connect to Chat Server
            _chatClient = await ConnectWithRetryAsync(remoteIP, CHAT_PORT, "Chat", maxRetries, retryDelay);
            
            // Connect to Speed Test Server
            _speedTestClient = await ConnectWithRetryAsync(remoteIP, SPEED_TEST_PORT, "SpeedTest", maxRetries, retryDelay);
            
            // Connect to File Server
            _fileClient = await ConnectWithRetryAsync(remoteIP, FILE_PORT, "File", maxRetries, retryDelay);
            
        }
        
        private async Task<TcpClient> ConnectWithRetryAsync(string remoteIP, int port, string serviceName, int maxRetries, TimeSpan retryDelay)
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
                        await Task.Delay(retryDelay, _cancellationTokenSource.Token);
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