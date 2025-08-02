using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WDCableWUI.Services;

namespace WDCableWUI.UI.Chat
{
    /// <summary>
    /// Page for chat communication with connected device.
    /// </summary>
    public sealed partial class ChatPage : Page
    {
        private WiFiDirectService _wifiDirectService;
        private ConnectionService _connectionService;
        
        public ChatPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            // Access services through ServiceManager
            _wifiDirectService = ServiceManager.WiFiDirectService;
            _connectionService = ServiceManager.ConnectionService;
            
            // Check if WiFi Direct is connected
            if (!ServiceManager.IsConnected)
            {
                // Show message that connection is required
                // TODO: Add UI element to show connection status
                return;
            }
            
            // Initialize chat functionality here
            InitializeChatConnection();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Cleanup when navigating away
        }
        
        private void InitializeChatConnection()
        {
            // Example of how to use the ConnectionService for chat
            if (_connectionService?.IsInitialized == true)
            {
                var chatConnection = _connectionService.ChatConnection;
                if (chatConnection != null && chatConnection.Connected)
                {
                    // Chat connection is ready
                    // TODO: Set up message sending/receiving
                }
            }
        }

        // TODO: Implement chat methods
        // - Send message
        // - Receive message
        // - Message history management
        // - Typing indicators
    }
}