using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
        private readonly ObservableCollection<ChatMessage> _messages;
        private bool _isConnected;
        
        public ChatPage()
        {
            InitializeComponent();
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _messages = new ObservableCollection<ChatMessage>();
            
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            try
            {
                // Access ChatService through ServiceManager with null checks
                _chatService = ServiceManager.IsInitialized ? ServiceManager.ChatService : null;
                
                // Subscribe to service events if ChatService is available
                if (_chatService != null)
                {
                    _chatService.StatusChanged += OnChatServiceStatusChanged;
                    _chatService.ErrorOccurred += OnChatServiceErrorOccurred;
                    _chatService.MessageReceived += OnMessageReceived;
                }
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
            
            // Unsubscribe from events
            if (_chatService != null)
            {
                _chatService.StatusChanged -= OnChatServiceStatusChanged;
                _chatService.ErrorOccurred -= OnChatServiceErrorOccurred;
                _chatService.MessageReceived -= OnMessageReceived;
            }
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
                _isConnected = ServiceManager.IsInitialized && ServiceManager.IsConnected && (_chatService?.IsConnected ?? false);
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
                    ConnectionStatusText.Text = "Connected";
                    ConnectionStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                    
                    // DisabledOverlay.Visibility = Visibility.Collapsed;
                    MessageTextBox.IsEnabled = true;
                    SendButton.IsEnabled = true;
                }
                else
                {
                    ConnectionStatusText.Text = "Disconnected";
                    ConnectionStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                    
                    // DisabledOverlay.Visibility = Visibility.Visible;
                    MessageTextBox.IsEnabled = false;
                    SendButton.IsEnabled = false;
                }
            });
        }
    
        private void OnChatServiceStatusChanged(object? sender, string status)
        {
            AddSystemMessage(status);
            UpdateConnectionStatus();
        }

        private void OnChatServiceErrorOccurred(object? sender, string error)
        {
            AddSystemMessage($"Error: {error}");
        }
        
        private void OnMessageReceived(object? sender, string message)
        {
            AddPeerMessage(message);
        }
        
        private void AddMessage(ChatMessage.MessageType type, string content)
        {
            var message = new ChatMessage
            {
                Type = type,
                Content = content,
                Timestamp = DateTime.Now
            };
            
            _dispatcherQueue.TryEnqueue(() =>
            {
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
        
        private void AddSelfMessage(string content)
        {
            AddMessage(ChatMessage.MessageType.Self, content);
        }
        
        private void AddPeerMessage(string content)
        {
            AddMessage(ChatMessage.MessageType.Peer, content);
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
        
        private void SendMessage()
        {
            var messageText = MessageTextBox.Text.Trim();
            if (string.IsNullOrEmpty(messageText) || !_isConnected)
            {
                return;
            }
            
            try
            {
                // Add message to UI immediately
                AddSelfMessage(messageText);
                
                // Clear input
                MessageTextBox.Text = string.Empty;
                
                // Send message through ChatService
                if (_chatService != null)
                {
                    _chatService.SendMessage(messageText);
                }
                else
                {
                    AddSystemMessage("ChatService is not available");
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Failed to send message: {ex.Message}");
            }
        }
        
        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }
        
        private void MessageTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                // Check if Shift is pressed for multi-line
                var shiftPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                
                if (!shiftPressed)
                {
                    e.Handled = true;
                    SendMessage();
                }
            }
        }
    }
}