using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WDCableWUI.Services;
using Windows.System;

namespace WDCableWUI.UI.Chat
{
    /// <summary>
    /// Represents a chat message with type and content.
    /// </summary>
    public class ChatMessage : INotifyPropertyChanged
    {
        public enum MessageType
        {
            Self,
            Peer,
            System
        }
        
        private string _content = string.Empty;
        private MessageType _type;
        private DateTime _timestamp;

        public string? MessageId { get; set; }
        
        public string Content
        {
            get => _content;
            set
            {
                _content = value;
                OnPropertyChanged();
            }
        }
        
        public MessageType Type
        {
            get => _type;
            set
            {
                _type = value;
                OnPropertyChanged();
            }
        }
        
        public DateTime Timestamp
        {
            get => _timestamp;
            set
            {
                _timestamp = value;
                OnPropertyChanged();
            }
        }
        
        public string TimeString => Timestamp.ToString("HH:mm");
        
        public event PropertyChangedEventHandler? PropertyChanged;
        
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    /// <summary>
    /// Page for chat communication with connected device.
    /// </summary>
    public sealed partial class ChatPage : Page
    {
        private ChatService? _chatService;
        private ChatService? _subscribedChatService;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
        private readonly ObservableCollection<ChatMessage> _messages;
        private bool _isConnected;
        private readonly DataManager _dataManager;
        
        public ChatPage()
        {
            InitializeComponent();
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _messages = new ObservableCollection<ChatMessage>();
            _dataManager = DataManager.Instance;
            Unloaded += OnPageUnloaded;
            
            LoadChatHistory();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            try
            {
                // Access ChatService through ServiceManager with null checks
                _chatService = ServiceManager.AreWiFiDirectServicesAvailable ? ServiceManager.ChatService : null;
                SubscribeToChatEvents();
                LoadChatHistory();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize ChatService: {ex.Message}");
                _chatService = null;
            }
            
            UpdateConnectionStatus();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            UnsubscribeFromChatEvents();
        }

        private void SubscribeToChatEvents()
        {
            if (_chatService == null || _subscribedChatService == _chatService)
            {
                return;
            }

            UnsubscribeFromChatEvents();

            _chatService.StatusChanged += OnChatServiceStatusChanged;
            _chatService.ErrorOccurred += OnChatServiceErrorOccurred;
            _chatService.ChatMessageReceived += OnMessageReceived;
            _subscribedChatService = _chatService;
        }

        private void UnsubscribeFromChatEvents()
        {
            if (_subscribedChatService == null)
            {
                return;
            }

            _subscribedChatService.StatusChanged -= OnChatServiceStatusChanged;
            _subscribedChatService.ErrorOccurred -= OnChatServiceErrorOccurred;
            _subscribedChatService.ChatMessageReceived -= OnMessageReceived;
            _subscribedChatService = null;
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeFromChatEvents();
        }
        
        private void SetupMessagesDisplay()
        {
            // We'll handle message display through code-behind instead of complex data templates
            // This approach is simpler and more reliable for our use case
        }
        
        private void UpdateConnectionStatus()
        {
            try
            {
                _isConnected = ServiceManager.AreWiFiDirectServicesAvailable &&
                    (ServiceManager.SessionManager?.IsReady ?? false) &&
                    (_chatService?.IsConnected ?? false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update connection status: {ex.Message}");
                _isConnected = false;
            }
            
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_isConnected)
                {
                    ConnectionStatusText.Text = "Ready";
                    ConnectionStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                    
                    // DisabledOverlay.Visibility = Visibility.Collapsed;
                    MessageTextBox.IsEnabled = true;
                    SendButton.IsEnabled = true;
                }
                else
                {
                    var wifiLinked = ServiceManager.IsConnected;
                    ConnectionStatusText.Text = ServiceManager.AreWiFiDirectServicesAvailable
                        ? (wifiLinked ? "Waiting for WDCable" : "Disconnected")
                        : "WiFi Direct unavailable";
                    ConnectionStatusText.Foreground = (Brush)Application.Current.Resources[
                        wifiLinked ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush"];
                    
                    // DisabledOverlay.Visibility = Visibility.Visible;
                    MessageTextBox.IsEnabled = false;
                    SendButton.IsEnabled = false;
                }
            });
        }
        
        /// <summary>
        /// Merges chat history from persistent storage into the current page.
        /// </summary>
        private void LoadChatHistory()
        {
            try
            {
                var chatData = _dataManager.LoadChatHistory();
                
                foreach (var data in chatData)
                {
                    if (ContainsMessage(data.MessageId, data.Type, data.Content, data.Timestamp))
                    {
                        continue;
                    }

                    var message = new ChatMessage
                    {
                        MessageId = data.MessageId,
                        Type = (ChatMessage.MessageType)data.Type,
                        Content = data.Content,
                        Timestamp = data.Timestamp
                    };
                    
                    _messages.Add(message);
                    
                    // Create UI element for the message
                    var messageElement = CreateMessageElement(message);
                    MessagesItemsControl.Items.Add(messageElement);
                }
                
                // Hide empty state if we have messages
                if (_messages.Count > 0)
                {
                    EmptyStatePanel.Visibility = Visibility.Collapsed;
                }
                
                // Auto-scroll to bottom after loading
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load chat history: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clears all chat history from both UI and persistent storage.
        /// </summary>
        public void ClearChatHistory()
        {
            try
            {
                _messages.Clear();
                MessagesItemsControl.Items.Clear();
                _dataManager.ClearChatHistory();
                EmptyStatePanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear chat history: {ex.Message}");
            }
        }
    
        private void OnChatServiceStatusChanged(object? sender, string status)
        {
            if (status != "Message sent")
            {
                AddSystemMessage(status);
            }
            UpdateConnectionStatus();
        }

        private void OnChatServiceErrorOccurred(object? sender, string error)
        {
            AddSystemMessage($"Error: {error}");
            UpdateConnectionStatus();
        }
        
        private void OnMessageReceived(object? sender, ChatMessageReceivedEventArgs e)
        {
            AddPeerMessage(
                e.Message.Message,
                e.Message.MessageId,
                e.Message.Timestamp.LocalDateTime);
        }
        
        private void AddMessage(
            ChatMessage.MessageType type,
            string content,
            string? messageId = null,
            DateTime? timestamp = null,
            bool persist = true)
        {
            var message = new ChatMessage
            {
                MessageId = messageId ?? Guid.NewGuid().ToString(),
                Type = type,
                Content = content,
                Timestamp = timestamp ?? DateTime.Now
            };

            if (persist)
            {
                _dataManager.UpsertChatMessage(new ChatMessageData
                {
                    MessageId = message.MessageId,
                    Type = (int)message.Type,
                    Content = message.Content,
                    Timestamp = message.Timestamp
                });
            }
            
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (ContainsMessage(
                        message.MessageId,
                        (int)message.Type,
                        message.Content,
                        message.Timestamp))
                {
                    return;
                }

                _messages.Add(message);
                
                // Create UI element for the message
                var messageElement = CreateMessageElement(message);
                MessagesItemsControl.Items.Add(messageElement);
                
                // Hide empty state when first message is added
                if (_messages.Count == 1)
                {
                    EmptyStatePanel.Visibility = Visibility.Collapsed;
                }
                
                // Auto-scroll to bottom
                ScrollToBottom();
            });
        }
        
        private FrameworkElement CreateMessageElement(ChatMessage message)
        {
            var grid = new Grid();
            grid.Margin = new Thickness(8, 4, 8, 4);
            
            var border = new Border();
            border.CornerRadius = new CornerRadius(12);
            border.Padding = new Thickness(12, 8, 12, 8);
            border.MaxWidth = 300;
            
            var textBlock = new TextBlock();
            textBlock.Text = message.Content;
            textBlock.TextWrapping = TextWrapping.Wrap;
            textBlock.IsTextSelectionEnabled = true;
            
            // Apply styles based on message type
            switch (message.Type)
            {
                case ChatMessage.MessageType.Self:
                    grid.HorizontalAlignment = HorizontalAlignment.Right;
                    border.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                    textBlock.Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
                    break;
                case ChatMessage.MessageType.Peer:
                    grid.HorizontalAlignment = HorizontalAlignment.Left;
                    border.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
                    textBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                    break;
                case ChatMessage.MessageType.System:
                    grid.HorizontalAlignment = HorizontalAlignment.Center;
                    border.Background = (Brush)Application.Current.Resources["SystemFillColorAttentionBrush"];
                    textBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                    textBlock.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    textBlock.FontSize = 12;
                    break;
            }
            
            border.Child = textBlock;
            grid.Children.Add(border);
            
            return grid;
        }
        
        private void AddSystemMessage(string content)
        {
            AddMessage(ChatMessage.MessageType.System, content);
        }
        
        private void AddSelfMessage(string content, string? messageId, DateTime timestamp)
        {
            AddMessage(ChatMessage.MessageType.Self, content, messageId, timestamp, persist: false);
        }

        private void AddPeerMessage(string content, string messageId, DateTime timestamp)
        {
            AddMessage(ChatMessage.MessageType.Peer, content, messageId, timestamp, persist: false);
        }

        private bool ContainsMessage(string? messageId, int type, string content, DateTime timestamp)
        {
            foreach (var existing in _messages)
            {
                if (!string.IsNullOrWhiteSpace(messageId) &&
                    string.Equals(existing.MessageId, messageId, StringComparison.Ordinal))
                {
                    return true;
                }

                if (string.IsNullOrWhiteSpace(messageId) &&
                    string.IsNullOrWhiteSpace(existing.MessageId) &&
                    (int)existing.Type == type &&
                    existing.Timestamp == timestamp &&
                    existing.Content == content)
                {
                    return true;
                }
            }

            return false;
        }
        
        private void ScrollToBottom()
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (MessagesScrollViewer.ScrollableHeight > 0)
                {
                    MessagesScrollViewer.ScrollToVerticalOffset(MessagesScrollViewer.ScrollableHeight);
                }
            });
        }
        
        private async Task SendMessageAsync()
        {
            var messageText = MessageTextBox.Text.Trim();
            if (string.IsNullOrEmpty(messageText) || !_isConnected)
            {
                return;
            }
            
            try
            {
                SendButton.IsEnabled = false;

                if (_chatService == null)
                {
                    AddSystemMessage("ChatService is not available");
                    return;
                }

                var result = await _chatService.SendMessageAsync(messageText);
                if (result.Success)
                {
                    AddSelfMessage(
                        messageText,
                        result.MessageId,
                        result.Timestamp?.LocalDateTime ?? DateTime.Now);
                    MessageTextBox.Text = string.Empty;
                }
                else
                {
                    AddSystemMessage(result.ErrorMessage ?? "Failed to send message");
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Failed to send message: {ex.Message}");
            }
            finally
            {
                UpdateConnectionStatus();
            }
        }
        
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }
        
        private async void MessageTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                // Check if Shift is pressed for multi-line
                var shiftPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                
                if (!shiftPressed)
                {
                    e.Handled = true;
                    await SendMessageAsync();
                }
            }
        }
        
        private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ClearChatHistory();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Failed to clear chat history: {ex.Message}");
            }
        }
    }
}
