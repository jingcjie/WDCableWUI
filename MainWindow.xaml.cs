using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WDCableWUI.UI.Connection;
using WDCableWUI.UI.Chat;
using WDCableWUI.UI.SpeedTest;
using WDCableWUI.UI.FileTransfer;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
using Windows.Graphics;
using System.Diagnostics;
using WDCableWUI.Services;

namespace WDCableWUI
{
    /// <summary>
    /// Main window for the WiFi Direct Cable application.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly Dictionary<string, Type> _pageTypes;
        
        public MainWindow()
        {
            InitializeComponent();
            
            // Configure custom title bar
            SetupCustomTitleBar();
            Debug.WriteLine("Sample Debug message: MainWindow initialized");
            // Initialize page type mappings
            _pageTypes = new Dictionary<string, Type>
            {
                { "Connection", typeof(ConnectionPage) },
                { "Chat", typeof(ChatPage) },
                { "SpeedTest", typeof(SpeedTestPage) },
                { "FileTransfer", typeof(FileTransferPage) }
            };
            
            // Set default page
            MainNavigationView.SelectedItem = MainNavigationView.MenuItems[0];
            NavigateToPage("Connection");
            
            // Initialize status
            UpdateConnectionStatus(false);
            
            // Subscribe to WiFiDirectService events
            SubscribeToWiFiDirectEvents();
        }
        
        private void SetupCustomTitleBar()
        {
            // Enable custom title bar
            ExtendsContentIntoTitleBar = true;
            
            // Set the drag region
            SetTitleBar(DragRegion);
            
            // Get the AppWindow for additional customization
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            
            
            
            // Customize title bar appearance
            if (appWindow.TitleBar != null)
            {
                appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }
        
        private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem selectedItem)
            {
                var tag = selectedItem.Tag?.ToString();
                if (!string.IsNullOrEmpty(tag))
                {
                    NavigateToPage(tag);
                    PageTitle.Text = selectedItem.Content?.ToString() ?? tag;
                }
            }
        }
        
        private void NavigateToPage(string pageTag)
        {
            if (_pageTypes.TryGetValue(pageTag, out Type? pageType))
            {
                ContentFrame.Navigate(pageType);
            }
        }
        
        private void SubscribeToWiFiDirectEvents()
        {
            if (ServiceManager.IsInitialized && ServiceManager.WiFiDirectService != null)
            {
                ServiceManager.WiFiDirectService.DeviceConnected += OnWiFiDirectDeviceConnected;
                ServiceManager.WiFiDirectService.DeviceDisconnected += OnWiFiDirectDeviceDisconnected;
            }
        }
        
        private void OnWiFiDirectDeviceConnected(object? sender, WiFiDirectDevice device)
        {
            // Ensure UI updates happen on the UI thread
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateConnectionStatus(true, device?.Name ?? "");
            });
        }
        
        private void OnWiFiDirectDeviceDisconnected(object? sender, EventArgs e)
        {
            // Ensure UI updates happen on the UI thread
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateConnectionStatus(false);
            });
        }
        
        public void UpdateConnectionStatus(bool isConnected, string deviceName = "")
        {
            if (isConnected)
            {
                ConnectionStatusIcon.Glyph = "\uE8FB"; // CheckMark
                ConnectionStatusIcon.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                ConnectionStatusText.Text = "Connected";
                DeviceInfo.Text = !string.IsNullOrEmpty(deviceName) ? $"Connected to {deviceName}" : "Device connected";
            }
            else
            {
                ConnectionStatusIcon.Glyph = "\uE703"; // Cancel
                ConnectionStatusIcon.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                ConnectionStatusText.Text = "Disconnected";
                DeviceInfo.Text = "No device connected";
            }
        }
        
        private void UnsubscribeFromWiFiDirectEvents()
        {
            if (ServiceManager.IsInitialized && ServiceManager.WiFiDirectService != null)
            {
                ServiceManager.WiFiDirectService.DeviceConnected -= OnWiFiDirectDeviceConnected;
                ServiceManager.WiFiDirectService.DeviceDisconnected -= OnWiFiDirectDeviceDisconnected;
            }
        }
        
    }
}
