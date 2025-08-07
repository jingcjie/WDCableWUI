using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
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
using WDCableWUI.UI.Settings;
using WDCableWUI.Services;
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
            
            // Apply saved theme
            ApplySavedTheme();
            
            // Configure custom title bar
            SetupCustomTitleBar();
            Debug.WriteLine("Sample Debug message: MainWindow initialized");
            // Initialize page type mappings
            _pageTypes = new Dictionary<string, Type>
            {
                { "Connection", typeof(ConnectionPage) },
                { "Chat", typeof(ChatPage) },
                { "SpeedTest", typeof(SpeedTestPage) },
                { "FileTransfer", typeof(FileTransferPage) },
                { "Settings", typeof(SettingsPage) }
            };
            

            
            // Set default page
            MainNavigationView.SelectedItem = MainNavigationView.MenuItems[0];
            NavigateToPage("Connection");
            PageTitle.Text = GetLocalizedPageTitle("Connection");
            
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
                    PageTitle.Text = GetLocalizedPageTitle(tag);
                }
            }
        }
        
        private void NavigateToPage(string pageTag)
        {
            if (_pageTypes.TryGetValue(pageTag, out Type? pageType))
            {
                // Use proper Frame navigation to ensure OnNavigatedTo/OnNavigatedFrom events are triggered
                // This will allow pages to update their connection status when navigated to
                ContentFrame.Navigate(pageType);
            }
        }
        
        private string GetLocalizedPageTitle(string pageTag)
        {
            try
            {
                var resourceLoader = Windows.ApplicationModel.Resources.ResourceLoader.GetForCurrentView();
                var resourceKey = $"MainWindow_PageTitle_{pageTag}";
                var localizedTitle = resourceLoader.GetString(resourceKey);
                return !string.IsNullOrEmpty(localizedTitle) ? localizedTitle : pageTag;
            }
            catch
            {
                return pageTag;
            }
        }
        
        private void SubscribeToWiFiDirectEvents()
        {
            try
            {
                if (ServiceManager.IsInitialized && ServiceManager.WiFiDirectService != null)
                {
                    ServiceManager.WiFiDirectService.DeviceConnected += OnWiFiDirectDeviceConnected;
                    ServiceManager.WiFiDirectService.DeviceDisconnected += OnWiFiDirectDeviceDisconnected;
                }
                
                if (ServiceManager.IsInitialized && ServiceManager.ConnectionService != null)
                {
                    ServiceManager.ConnectionService.OtherSideNotRunningApp += OnOtherSideNotRunningApp;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to subscribe to WiFiDirect events: {ex.Message}");
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
            try
            {
                if (ServiceManager.IsInitialized && ServiceManager.WiFiDirectService != null)
                {
                    ServiceManager.WiFiDirectService.DeviceConnected -= OnWiFiDirectDeviceConnected;
                    ServiceManager.WiFiDirectService.DeviceDisconnected -= OnWiFiDirectDeviceDisconnected;
                }
                
                if (ServiceManager.IsInitialized && ServiceManager.ConnectionService != null)
                {
                    ServiceManager.ConnectionService.OtherSideNotRunningApp -= OnOtherSideNotRunningApp;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to unsubscribe from WiFiDirect events: {ex.Message}");
            }
        }
        
        private async void OnOtherSideNotRunningApp(object? sender, EventArgs e)
        {
            // Ensure UI updates happen on the UI thread
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowOtherSideNotRunningAppDialog();
            });
        }
        
        private async Task ShowOtherSideNotRunningAppDialog()
        {
            try
            {
                var dialog = new ContentDialog()
                {
                    Title = "Connection Issue",
                    Content = "The other device does not appear to be running this application. The connection will be terminated.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to show dialog: {ex.Message}");
            }
        }
        
        private void ApplySavedTheme()
        {
            try
            {
                string savedTheme = "default";
                
                // Try to get theme from DataManager first
                try
                {
                    if (ServiceManager.IsInitialized && ServiceManager.DataManager != null)
                    {
                        savedTheme = ServiceManager.DataManager.GetAppTheme();
                    }
                    else
                    {
                        // Fallback to direct access if DataManager is not available
                        var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                        savedTheme = localSettings.Values["AppTheme"] as string ?? "default";
                    }
                }
                catch
                {
                    // Fallback to direct access
                    var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                    savedTheme = localSettings.Values["AppTheme"] as string ?? "default";
                }
                
                if (this.Content is FrameworkElement rootElement)
                {
                    switch (savedTheme)
                    {
                        case "light":
                            rootElement.RequestedTheme = ElementTheme.Light;
                            break;
                        case "dark":
                            rootElement.RequestedTheme = ElementTheme.Dark;
                            break;
                        case "default":
                        default:
                            rootElement.RequestedTheme = ElementTheme.Default;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to apply saved theme: {ex.Message}");
            }
        }
    }
}
