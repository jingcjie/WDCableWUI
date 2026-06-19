using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WDCableWUI.UI.Connection;
using WDCableWUI.UI.Chat;
using WDCableWUI.UI.SpeedTest;
using WDCableWUI.UI.FileTransfer;
using WDCableWUI.UI.Audio;
using WDCableWUI.UI.Settings;
using WDCableWUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
using System.Diagnostics;
using Windows.ApplicationModel;

namespace WDCableWUI
{
    /// <summary>
    /// Main window for the WiFi Direct Cable application.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly Dictionary<string, Type> _pageTypes;
        private SessionManager? _subscribedSessionManager;
        private AppWindow? _appWindow;
        private bool _connectionPromptActive;
        private bool _windowShutdownInProgress;
        private bool _allowWindowClose;
        
        public MainWindow()
        {
            InitializeComponent();
            
            // Apply saved theme
            ApplySavedTheme();
            
            // Configure custom title bar
            SetupCustomTitleBar();
            // Initialize page type mappings
            _pageTypes = new Dictionary<string, Type>
            {
                { "Connection", typeof(ConnectionPage) },
                { "Chat", typeof(ChatPage) },
                { "SpeedTest", typeof(SpeedTestPage) },
                { "FileTransfer", typeof(FileTransferPage) },
                { "Audio", typeof(AudioPage) },
                { "Settings", typeof(SettingsPage) }
            };
            
            ContentFrame.NavigationFailed += OnContentFrameNavigationFailed;

            
            // Set default page
            MainNavigationView.SelectedItem = MainNavigationView.MenuItems[0];
            NavigateToPage("Connection");
            PageTitle.Text = GetLocalizedPageTitle("Connection");
            
            // Initialize status
            if (ServiceManager.AreWiFiDirectServicesAvailable)
            {
                UpdateConnectionStatus(false);
            }
            else
            {
                UpdateServiceUnavailableStatus();
            }
            
            // Subscribe to WiFiDirect/session events
            SubscribeToWiFiDirectEvents();
            _ = EnsureDiscoverableOnStartupAsync();
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
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Closing += OnAppWindowClosing;
            SetWindowIcon(_appWindow);
            
            // Customize title bar appearance
            if (_appWindow.TitleBar != null)
            {
                _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }

        private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowWindowClose)
            {
                sender.Closing -= OnAppWindowClosing;
                return;
            }

            args.Cancel = true;
            if (_windowShutdownInProgress)
            {
                return;
            }

            _windowShutdownInProgress = true;
            UnsubscribeFromWiFiDirectEvents();

            try
            {
                await ServiceManager.ShutdownAsync("window_closing");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Window shutdown cleanup failed: {ex}");
            }
            finally
            {
                _allowWindowClose = true;
                Close();
            }
        }

        private static void SetWindowIcon(AppWindow appWindow)
        {
            foreach (var iconPath in GetWindowIconPaths())
            {
                if (!File.Exists(iconPath))
                {
                    continue;
                }

                try
                {
                    appWindow.SetIcon(iconPath);
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to set window icon from '{iconPath}': {ex.Message}");
                }
            }
        }

        private static IEnumerable<string> GetWindowIconPaths()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "Assets", "WDCable.ico");
            yield return Path.Combine(AppContext.BaseDirectory, "AppX", "Assets", "WDCable.ico");
            yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Assets", "WDCable.ico"));

            string? packageIconPath = null;
            try
            {
                packageIconPath = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "WDCable.ico");
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(packageIconPath))
            {
                yield return packageIconPath;
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
                try
                {
                    // Use proper Frame navigation to ensure OnNavigatedTo/OnNavigatedFrom events are triggered
                    // This will allow pages to update their connection status when navigated to
                    ContentFrame.Navigate(pageType);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Navigation to {pageTag} failed: {ex}");
                    StatusMessage.Text = $"Could not open {pageTag}";
                }
            }
        }

        private void OnContentFrameNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            Debug.WriteLine($"Frame navigation failed for {e.SourcePageType}: {e.Exception}");
            StatusMessage.Text = "Page navigation failed";
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
                var wifiDirectService = ServiceManager.WiFiDirectService;
                if (ServiceManager.AreWiFiDirectServicesAvailable && wifiDirectService != null)
                {
                    wifiDirectService.DeviceConnected += OnWiFiDirectDeviceConnected;
                    wifiDirectService.DeviceDisconnected += OnWiFiDirectDeviceDisconnected;
                    wifiDirectService.ConnectionRequested += OnWiFiDirectConnectionRequested;
                }
                
                var sessionManager = ServiceManager.SessionManager;
                if (ServiceManager.AreWiFiDirectServicesAvailable && sessionManager != null)
                {
                    sessionManager.StateChanged += OnSessionStateChanged;
                    sessionManager.SessionReady += OnSessionReady;
                    sessionManager.SessionFailed += OnSessionFailed;
                    sessionManager.PeerProtocolMissing += OnPeerProtocolMissing;
                    _subscribedSessionManager = sessionManager;
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
                UpdateWiFiDirectLinkedStatus(device?.Name ?? "");
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

        private async void OnWiFiDirectConnectionRequested(object? sender, ConnectionRequestEventArgs e)
        {
            if (_connectionPromptActive)
            {
                e.ResponseTask.TrySetResult(false);
                return;
            }

            _connectionPromptActive = true;
            try
            {
                var accepted = await ShowConnectionRequestDialog(e.RequestingDevice.Name);
                e.ResponseTask.TrySetResult(accepted);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to show WiFi Direct connection request dialog: {ex.Message}");
                e.ResponseTask.TrySetResult(false);
            }
            finally
            {
                _connectionPromptActive = false;
            }
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

        private void UpdateWiFiDirectLinkedStatus(string deviceName = "")
        {
            ConnectionStatusIcon.Glyph = "\uE783"; // Warning
            ConnectionStatusIcon.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCautionBrush"];
            ConnectionStatusText.Text = "WiFi Linked";
            DeviceInfo.Text = !string.IsNullOrEmpty(deviceName) ? $"WiFi Direct linked to {deviceName}" : "WiFi Direct linked";
            StatusMessage.Text = "Negotiating WDCable session";
        }

        private void UpdateSessionReadyStatus(SessionReadyEventArgs e)
        {
            ConnectionStatusIcon.Glyph = "\uE8FB"; // CheckMark
            ConnectionStatusIcon.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            ConnectionStatusText.Text = "Ready";
            DeviceInfo.Text = !string.IsNullOrEmpty(e.PeerName) ? $"Ready with {e.PeerName}" : "WDCable session ready";
            StatusMessage.Text = $"WDCable protocol v{e.ProtocolVersion} ready";
        }

        private void UpdateSessionPhaseStatus(SessionStateChangedEventArgs e)
        {
            if (e.Phase == SessionPhase.Ready || e.Phase == SessionPhase.Disconnected)
            {
                return;
            }

            ConnectionStatusIcon.Glyph = "\uE783"; // Warning
            ConnectionStatusIcon.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCautionBrush"];
            ConnectionStatusText.Text = e.StateName;
            DeviceInfo.Text = !string.IsNullOrEmpty(e.PeerName) ? e.PeerName : "WiFi Direct peer linked";
            StatusMessage.Text = e.Phase switch
            {
                SessionPhase.WifiDirectConnected => "WiFi Direct linked; starting WDCable session",
                SessionPhase.ConnectingTransport => "Opening WDCable transport channels",
                SessionPhase.Handshaking => "Negotiating WDCable protocol",
                SessionPhase.Failed => e.DisconnectReason ?? "WDCable session failed",
                _ => e.StateName
            };
        }

        private void UpdateSessionFailedStatus(SessionFailedEventArgs e)
        {
            ConnectionStatusIcon.Glyph = "\uE783"; // Warning
            ConnectionStatusIcon.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCautionBrush"];
            ConnectionStatusText.Text = "Protocol failed";
            DeviceInfo.Text = e.Reason;
            StatusMessage.Text = e.Message;
        }

        private void UpdateServiceUnavailableStatus()
        {
            ConnectionStatusIcon.Glyph = "\uE783"; // Warning
            ConnectionStatusIcon.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCautionBrush"];
            ConnectionStatusText.Text = "WiFi Direct unavailable";
            DeviceInfo.Text = "Settings remain available";
            StatusMessage.Text = ServiceManager.ServiceUnavailableMessage;
        }
        
        private void UnsubscribeFromWiFiDirectEvents()
        {
            try
            {
                var wifiDirectService = ServiceManager.WiFiDirectService;
                if (wifiDirectService != null)
                {
                    wifiDirectService.DeviceConnected -= OnWiFiDirectDeviceConnected;
                    wifiDirectService.DeviceDisconnected -= OnWiFiDirectDeviceDisconnected;
                    wifiDirectService.ConnectionRequested -= OnWiFiDirectConnectionRequested;
                }
                
                if (_subscribedSessionManager != null)
                {
                    _subscribedSessionManager.StateChanged -= OnSessionStateChanged;
                    _subscribedSessionManager.SessionReady -= OnSessionReady;
                    _subscribedSessionManager.SessionFailed -= OnSessionFailed;
                    _subscribedSessionManager.PeerProtocolMissing -= OnPeerProtocolMissing;
                    _subscribedSessionManager = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to unsubscribe from WiFiDirect events: {ex.Message}");
            }
        }
        
        private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => UpdateSessionPhaseStatus(e));
        }

        private void OnSessionReady(object? sender, SessionReadyEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => UpdateSessionReadyStatus(e));
        }

        private void OnSessionFailed(object? sender, SessionFailedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => UpdateSessionFailedStatus(e));
        }

        private void OnPeerProtocolMissing(object? sender, SessionFailedEventArgs e)
        {
            // Ensure UI updates happen on the UI thread
            DispatcherQueue.TryEnqueue(async () =>
            {
                UpdateSessionFailedStatus(e);
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

        private async Task<bool> ShowConnectionRequestDialog(string deviceName)
        {
            var dialog = new ContentDialog()
            {
                Title = "Connection Request",
                Content = $"'{deviceName}' wants to connect to your device.\n\nDo you want to accept this connection?",
                PrimaryButtonText = "Accept",
                SecondaryButtonText = "Decline",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async Task EnsureDiscoverableOnStartupAsync()
        {
            await Task.Yield();

            var wifiDirectService = ServiceManager.WiFiDirectService;
            if (!ServiceManager.AreWiFiDirectServicesAvailable || wifiDirectService == null)
            {
                return;
            }

            var started = await wifiDirectService.EnsureDiscoverableAsync("app_startup");
            if (!started)
            {
                DispatcherQueue.TryEnqueue(() => StatusMessage.Text = wifiDirectService.DiscoverabilityStatus);
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
