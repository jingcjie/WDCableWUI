using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace WDCableWUI.Services
{
    /// <summary>
    /// Compatibility facade for older pages while SessionManager owns all transport lifecycle.
    /// Feature services should migrate to SessionManager APIs instead of using socket properties.
    /// </summary>
    public class ConnectionService : IDisposable
    {
        private static ConnectionService? _instance;
        private static readonly object Lock = new();

        private readonly DispatcherQueue? _dispatcherQueue;
        private SessionManager? _sessionManager;
        private bool _isDisposed;

        private ConnectionService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        public static ConnectionService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (Lock)
                    {
                        _instance ??= new ConnectionService();
                    }
                }

                return _instance;
            }
        }

        public static void ResetInstance()
        {
            lock (Lock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? ConnectionsEstablished;
        public event EventHandler? OtherSideNotRunningApp;

        public bool IsInitialized => _sessionManager != null;

        public bool IsGroupOwner => _sessionManager?.CurrentRole == SessionRole.GroupOwner;

        public bool IsConnected => _sessionManager?.IsReady ?? false;

        public TcpClient? ChatConnection => null;

        public TcpClient? SpeedTestConnection => null;

        public TcpClient? FileConnection => null;

        public void Initialize(WiFiDirectService wifiDirectService)
        {
            Initialize(wifiDirectService, ServiceManager.SessionManager ?? WDCableWUI.Services.SessionManager.Instance);
        }

        public void Initialize(WiFiDirectService wifiDirectService, SessionManager sessionManager)
        {
            ArgumentNullException.ThrowIfNull(wifiDirectService);
            ArgumentNullException.ThrowIfNull(sessionManager);

            if (_sessionManager != null)
            {
                UnsubscribeFromSession(_sessionManager);
            }

            _sessionManager = sessionManager;
            SubscribeToSession(_sessionManager);
        }

        public bool IsConnectionHealthy()
        {
            return _sessionManager?.IsReady ?? false;
        }

        public Task InitializeConnectionsAsync()
        {
            return _sessionManager?.StartFromCurrentWiFiDirectLinkAsync() ?? Task.CompletedTask;
        }

        public void CleanupConnections()
        {
            if (_sessionManager != null)
            {
                _ = _sessionManager.DisconnectAsync("connection_service_cleanup");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (_sessionManager != null)
            {
                UnsubscribeFromSession(_sessionManager);
                _sessionManager = null;
            }
        }

        private void SubscribeToSession(SessionManager sessionManager)
        {
            sessionManager.StatusChanged += OnSessionStatusChanged;
            sessionManager.ErrorOccurred += OnSessionErrorOccurred;
            sessionManager.SessionReady += OnSessionReady;
            sessionManager.PeerProtocolMissing += OnPeerProtocolMissing;
        }

        private void UnsubscribeFromSession(SessionManager sessionManager)
        {
            sessionManager.StatusChanged -= OnSessionStatusChanged;
            sessionManager.ErrorOccurred -= OnSessionErrorOccurred;
            sessionManager.SessionReady -= OnSessionReady;
            sessionManager.PeerProtocolMissing -= OnPeerProtocolMissing;
        }

        private void OnSessionStatusChanged(object? sender, string status)
        {
            OnStatusChanged(status);
        }

        private void OnSessionErrorOccurred(object? sender, string error)
        {
            OnErrorOccurred(error);
        }

        private void OnSessionReady(object? sender, SessionReadyEventArgs e)
        {
            OnStatusChanged("WDCable session ready - Services are now available");
            OnConnectionsEstablished();
        }

        private void OnPeerProtocolMissing(object? sender, SessionFailedEventArgs e)
        {
            OnStatusChanged("Peer is connected by WiFi Direct but is not running the upgraded WDCable protocol");
            OnOtherSideNotRunningApp();
        }

        private void OnStatusChanged(string status)
        {
            RaiseOnDispatcher(() => StatusChanged?.Invoke(this, status));
        }

        private void OnErrorOccurred(string error)
        {
            RaiseOnDispatcher(() => ErrorOccurred?.Invoke(this, error));
        }

        private void OnConnectionsEstablished()
        {
            RaiseOnDispatcher(() => ConnectionsEstablished?.Invoke(this, EventArgs.Empty));
        }

        private void OnOtherSideNotRunningApp()
        {
            RaiseOnDispatcher(() => OtherSideNotRunningApp?.Invoke(this, EventArgs.Empty));
        }

        private void RaiseOnDispatcher(Action action)
        {
            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(() => action());
            }
            else
            {
                action();
            }
        }
    }
}
