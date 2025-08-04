using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace WDCableWUI.Services
{
    /// <summary>
    /// Singleton service for managing chat functionality.
    /// Provides access to the current chat connection through the WiFiDirectService.
    /// </summary>
    public class ChatService : IDisposable
    {
        private static ChatService? _instance;
        private static readonly object _lock = new object();
        
        private readonly DispatcherQueue _dispatcherQueue;
        private bool _isDisposed = false;
        private CancellationTokenSource? _listeningCancellationTokenSource;
        private Task? _listeningTask;
        
        // Events
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? MessageReceived;
        
        /// <summary>
        /// Gets the singleton ChatService instance.
        /// </summary>
        public static ChatService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new ChatService();
                        }
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Gets the current chat connection from the ConnectionService.
        /// Returns null if no connection is available or if the connection is no longer valid.
        /// </summary>
        public TcpClient? ChatConnection
        {
            get
            {
                try
                {
                    var connectionService = ServiceManager.ConnectionService;
                    if (connectionService == null)
                    {
                        return null;
                    }
                    
                    var chatConnection = connectionService.ChatConnection;
                    
                    // Check if connection is still valid
                    if (chatConnection != null && !IsConnectionValid(chatConnection))
                    {
                        OnStatusChanged("Chat connection is no longer valid, attempting to re-obtain connection");
                        // The connection will be re-established by the ConnectionService
                        return null;
                    }
                    
                    return chatConnection;
                }
                catch (Exception ex)
                {
                    OnErrorOccurred($"Error obtaining chat connection: {ex.Message}");
                    return null;
                }
            }
        }
        
        /// <summary>
        /// Gets whether a valid chat connection is currently available.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                var connection = ChatConnection;
                return connection != null && IsConnectionValid(connection);
            }
        }
        
        /// <summary>
        /// Private constructor to enforce singleton pattern.
        /// </summary>
        private ChatService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            
            // Subscribe to ConnectionService events
            var connectionService = ServiceManager.ConnectionService;
            if (connectionService != null)
            {
                connectionService.ConnectionsEstablished += OnConnectionsEstablished;
            }
            
            OnStatusChanged("ChatService initialized");
        }
        
        /// <summary>
        /// Checks if a TCP connection is still valid and connected.
        /// </summary>
        /// <param name="connection">The TCP connection to check</param>
        /// <returns>True if the connection is valid and connected, false otherwise</returns>
        private bool IsConnectionValid(TcpClient connection)
        {
            try
            {
                if (connection == null || !connection.Connected)
                {
                    return false;
                }
                
                // Additional check using socket polling
                var socket = connection.Client;
                if (socket == null)
                {
                    return false;
                }
                
                // Poll the socket to check if it's still connected
                bool part1 = socket.Poll(1000, SelectMode.SelectRead);
                bool part2 = (socket.Available == 0);
                
                // If both conditions are true, the connection is closed
                return !(part1 && part2);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Raises the StatusChanged event.
        /// </summary>
        /// <param name="status">The status message</param>
        private void OnStatusChanged(string status)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => StatusChanged?.Invoke(this, status));
            }
            catch
            {
                // Ignore dispatcher errors during status updates
            }
        }
        
        /// <summary>
        /// Raises the ErrorOccurred event.
        /// </summary>
        /// <param name="error">The error message</param>
        private void OnErrorOccurred(string error)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => ErrorOccurred?.Invoke(this, error));
            }
            catch
            {
                // Ignore dispatcher errors during error reporting
            }
        }
        
        /// <summary>
        /// Handles the ConnectionsEstablished event from ConnectionService.
        /// </summary>
        /// <param name="sender">The event sender</param>
        /// <param name="e">The event arguments</param>
        private void OnConnectionsEstablished(object? sender, EventArgs e)
        {
            OnStatusChanged("TCP connections established - Chat is now available");
            
            // Automatically start listening when connections are established
            StartListening();
        }
        
        /// <summary>
        /// Raises the MessageReceived event.
        /// </summary>
        /// <param name="message">The received message</param>
        private void OnMessageReceived(string message)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => {
                    MessageReceived?.Invoke(this, message);
                });
            }
            catch (Exception ex)
            {
                // Ignore dispatcher errors during message delivery
            }
        }
        
        /// <summary>
        /// Sends a message through the chat connection.
        /// </summary>
        /// <param name="message">The message to send</param>
        public void SendMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                OnErrorOccurred("Cannot send empty message");
                return;
            }
            
            var connection = ChatConnection;
            if (connection == null || !IsConnectionValid(connection))
            {
                OnErrorOccurred("No valid chat connection available");
                return;
            }
            
            try
            {
                var stream = connection.GetStream();
                
                // Create JSON message with timestamp
                var jsonMessage = new
                {
                    message = message,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                // Serialize to JSON and add newline terminator
                var jsonString = JsonSerializer.Serialize(jsonMessage) + "\n";
                var messageBytes = Encoding.UTF8.GetBytes(jsonString);
                
                // Send the message
                stream.Write(messageBytes, 0, messageBytes.Length);
                stream.Flush();
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to send message: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Starts listening for incoming messages on the chat connection.
        /// This method should be called when a connection is established.
        /// </summary>
        public void StartListening()
        {
            var connection = ChatConnection;
            if (connection == null || !IsConnectionValid(connection))
            {
                OnErrorOccurred("No valid chat connection available for listening");
                return;
            }
            
            try
            {
                // Stop any existing listening task
                StopListening();
                
                // Create new cancellation token
                _listeningCancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = _listeningCancellationTokenSource.Token;
                
                // Start listening task
                _listeningTask = Task.Run(async () =>
                {
                    try
                    {
                        var stream = connection.GetStream();
                        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                        
                        while (!cancellationToken.IsCancellationRequested && connection.Connected)
                        {
                            var line = await reader.ReadLineAsync();
                            if (line != null)
                            {
                                try
                                {
                                    // Try to parse as JSON first
                                    var jsonDoc = JsonDocument.Parse(line);
                                    if (jsonDoc.RootElement.TryGetProperty("message", out var messageElement))
                                    {
                                        var messageContent = messageElement.GetString();
                                        if (!string.IsNullOrEmpty(messageContent))
                                        {
                                            OnMessageReceived(messageContent);
                                        }
                                    }
                                }
                                catch (JsonException ex)
                                {
                                    // Fallback to plain text for backward compatibility
                                    OnMessageReceived(line);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        OnErrorOccurred($"Error in message listener: {ex.Message}");
                    }
                }, cancellationToken);
                
                OnStatusChanged("Started listening for messages");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to start listening: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Stops listening for incoming messages.
        /// </summary>
        public void StopListening()
        {
            try
            {
                // Cancel the listening task
                _listeningCancellationTokenSource?.Cancel();
                
                // Wait for the task to complete (with timeout)
                if (_listeningTask != null && !_listeningTask.IsCompleted)
                {
                    try
                    {
                        _listeningTask.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (AggregateException)
                    {
                        // Task was cancelled, which is expected
                    }
                }
                
                // Clean up resources
                _listeningCancellationTokenSource?.Dispose();
                _listeningCancellationTokenSource = null;
                _listeningTask = null;
                
                OnStatusChanged("Stopped listening for messages");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error stopping listener: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Disposes the ChatService and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            
            try
            {
                // Stop listening for messages
                StopListening();
                
                // Unsubscribe from ConnectionService events
                var connectionService = ServiceManager.ConnectionService;
                if (connectionService != null)
                {
                    connectionService.ConnectionsEstablished -= OnConnectionsEstablished;
                }
                
                OnStatusChanged("ChatService disposing");
                _isDisposed = true;
            }
            catch
            {
                // Ignore errors during disposal
            }
        }
        
        /// <summary>
        /// Resets the singleton instance. Used for testing or service restart scenarios.
        /// </summary>
        public static void ResetInstance()
        {
            lock (_lock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }
    }
}