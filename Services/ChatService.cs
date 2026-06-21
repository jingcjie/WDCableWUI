using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace WDCableWUI.Services
{
    /// <summary>
    /// Chat service backed by the upgraded WDCable control channel.
    /// </summary>
    public class ChatService : IDisposable
    {
        private static ChatService? _instance;
        private static readonly object Lock = new();

        private readonly DispatcherQueue? _dispatcherQueue;
        private SessionManager? _sessionManager;
        private bool _isDisposed;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? MessageReceived;
        public event EventHandler<ChatMessageReceivedEventArgs>? ChatMessageReceived;

        public static ChatService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (Lock)
                    {
                        _instance ??= new ChatService();
                    }
                }

                return _instance;
            }
        }

        public bool IsConnected => _sessionManager?.IsReady ?? false;

        private ChatService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _sessionManager = ServiceManager.SessionManager;
            if (_sessionManager != null)
            {
                SubscribeToSession(_sessionManager);
            }

            OnStatusChanged("ChatService initialized");
        }

        public async Task<ChatSendResult> SendMessageAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                const string error = "Cannot send empty message";
                OnErrorOccurred(error);
                return new ChatSendResult(false, errorMessage: error);
            }

            if (_sessionManager?.IsReady != true || string.IsNullOrWhiteSpace(_sessionManager.CurrentSessionId))
            {
                const string error = "WDCable session is not ready";
                OnErrorOccurred(error);
                return new ChatSendResult(false, errorMessage: error);
            }

            try
            {
                var messageId = Guid.NewGuid();
                var timestamp = DateTimeOffset.UtcNow;
                var frame = ChatProtocol.CreateFrame(
                    _sessionManager.CurrentSessionId,
                    message,
                    messageId,
                    timestamp);

                await _sessionManager.SendControlFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                ServiceManager.DataManager?.UpsertChatMessage(new ChatMessageData
                {
                    MessageId = messageId.ToString(),
                    Type = 0,
                    Content = message,
                    Timestamp = timestamp.LocalDateTime
                });
                OnStatusChanged("Message sent");
                return new ChatSendResult(true, messageId.ToString(), timestamp);
            }
            catch (Exception ex)
            {
                var error = $"Failed to send chat message: {ex.Message}";
                OnErrorOccurred(error);
                return new ChatSendResult(false, errorMessage: error);
            }
        }

        public void SendMessage(string message)
        {
            _ = SendMessageAsync(message);
        }

        public void StartListening()
        {
            OnStatusChanged(IsConnected ? "Chat control channel is ready" : "Waiting for WDCable session readiness");
        }

        public void StopListening()
        {
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

        public static void ResetInstance()
        {
            lock (Lock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }

        private void SubscribeToSession(SessionManager sessionManager)
        {
            sessionManager.SessionReady += OnSessionReady;
            sessionManager.SessionFailed += OnSessionFailed;
            sessionManager.StateChanged += OnSessionStateChanged;
            sessionManager.ControlFrameReceived += OnControlFrameReceived;
        }

        private void UnsubscribeFromSession(SessionManager sessionManager)
        {
            sessionManager.SessionReady -= OnSessionReady;
            sessionManager.SessionFailed -= OnSessionFailed;
            sessionManager.StateChanged -= OnSessionStateChanged;
            sessionManager.ControlFrameReceived -= OnControlFrameReceived;
        }

        private void OnSessionReady(object? sender, SessionReadyEventArgs e)
        {
            OnStatusChanged("WDCable session ready - Chat is available");
        }

        private void OnSessionFailed(object? sender, SessionFailedEventArgs e)
        {
            OnErrorOccurred(e.IsPeerProtocolMissing
                ? "Peer is connected by WiFi Direct but is not running the upgraded WDCable protocol"
                : $"WDCable session failed: {e.Message}");
        }

        private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
        {
            if (e.Phase == SessionPhase.Disconnected)
            {
                OnStatusChanged("WDCable session disconnected");
            }
            else if (e.Phase is SessionPhase.WifiDirectConnected or SessionPhase.ConnectingTransport or SessionPhase.Handshaking)
            {
                OnStatusChanged($"WDCable session {e.StateName}");
            }
        }

        private void OnControlFrameReceived(object? sender, ProtocolFrameReceivedEventArgs e)
        {
            if (!ChatProtocol.TryParseFrame(e.Frame, out var chatMessage) || chatMessage == null)
            {
                return;
            }

            ServiceManager.DataManager?.UpsertChatMessage(new ChatMessageData
            {
                MessageId = chatMessage.MessageId,
                Type = 1,
                Content = chatMessage.Message,
                Timestamp = chatMessage.Timestamp.LocalDateTime
            });

            RaiseOnDispatcher(() =>
            {
                ChatMessageReceived?.Invoke(this, new ChatMessageReceivedEventArgs(chatMessage));
                MessageReceived?.Invoke(this, chatMessage.Message);
            });
        }

        private void OnStatusChanged(string status)
        {
            RaiseOnDispatcher(() => StatusChanged?.Invoke(this, status));
        }

        private void OnErrorOccurred(string error)
        {
            RaiseOnDispatcher(() => ErrorOccurred?.Invoke(this, error));
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
