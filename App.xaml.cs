using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WDCableWUI.Services;
using Windows.ApplicationModel.Resources;
using Windows.Globalization;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WDCableWUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        
        public Window? Window => _window;
        public static MainWindow? MainWindow { get; private set; }
        
        private bool _serviceInitializationFailed = false;
        private string _serviceInitializationError = string.Empty;
        
        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            
            // Initialize localization
            InitializeLocalization();
            
            // Initialize theme
            InitializeTheme();
            
            // Initialize ServiceManager during app startup with proper error handling
            try
            {
                ServiceManager.Initialize();
                if (!ServiceManager.AreWiFiDirectServicesAvailable)
                {
                    _serviceInitializationFailed = true;
                    _serviceInitializationError = ServiceManager.ServiceUnavailableMessage;
                }
            }
            catch (NotSupportedException ex)
            {
                // WiFi Direct not supported - show user-friendly message
                System.Diagnostics.Debug.WriteLine($"WiFi Direct not supported: {ex.Message}");
                _serviceInitializationFailed = true;
                _serviceInitializationError = ex.Message;
                // Log the error but continue app startup
                // Services will be null but the app should still function
            }
            catch (Exception ex)
            {
                // Handle other initialization errors gracefully - don't crash the app
                System.Diagnostics.Debug.WriteLine($"Failed to initialize ServiceManager: {ex.Message}");
                _serviceInitializationFailed = true;
                _serviceInitializationError = $"WiFi Direct services could not be initialized: {ex.Message}";
                // Log the error but continue app startup
                // Services will be null but the app should still function
            }
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LogUnhandledException("Application.UnhandledException", e.Exception);
        }

        private static void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            LogUnhandledException("AppDomain.UnhandledException", e.ExceptionObject);
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogUnhandledException("TaskScheduler.UnobservedTaskException", e.Exception);
        }

        private static void LogUnhandledException(string source, object? exception)
        {
            try
            {
                var message = $"{DateTimeOffset.Now:O} [{source}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "wdcable-crash.log"), message);
                Debug.WriteLine(message);
            }
            catch
            {
            }
        }

        private void InitializeLocalization()
        {
            try
            {
                string? savedLanguage = null;
                
                // Try to get language from DataManager first, but since this runs early,
                // we need to fallback to direct access
                try
                {
                    if (ServiceManager.IsInitialized && ServiceManager.DataManager != null)
                    {
                        savedLanguage = ServiceManager.DataManager.GetAppLanguage();
                    }
                    else
                    {
                        // Fallback to direct access since ServiceManager may not be initialized yet
                        var localSettings = ApplicationData.Current.LocalSettings;
                        savedLanguage = localSettings.Values["AppLanguage"] as string;
                    }
                }
                catch
                {
                    // Fallback to direct access
                    var localSettings = ApplicationData.Current.LocalSettings;
                    savedLanguage = localSettings.Values["AppLanguage"] as string;
                }
                
                System.Diagnostics.Debug.WriteLine($"Initializing localization with saved language: {savedLanguage}");
                
                if (!string.IsNullOrEmpty(savedLanguage) && savedLanguage != "system")
                {
                    ApplicationLanguages.PrimaryLanguageOverride = savedLanguage;
                    System.Diagnostics.Debug.WriteLine($"Applied language override: {savedLanguage}");
                }
                else
                {
                    // Clear any previous language override to use system language
                    ApplicationLanguages.PrimaryLanguageOverride = "";
                    System.Diagnostics.Debug.WriteLine("Using system language");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize localization: {ex.Message}");
            }
        }
        
        private void InitializeTheme()
        {
            try
            {
                string? savedTheme = null;
                
                // Try to get theme from DataManager first, but since this runs early,
                // we need to fallback to direct access
                try
                {
                    if (ServiceManager.IsInitialized && ServiceManager.DataManager != null)
                    {
                        savedTheme = ServiceManager.DataManager.GetAppTheme();
                    }
                    else
                    {
                        // Fallback to direct access since ServiceManager may not be initialized yet
                        var localSettings = ApplicationData.Current.LocalSettings;
                        savedTheme = localSettings.Values["AppTheme"] as string;
                    }
                }
                catch
                {
                    // Fallback to direct access
                    var localSettings = ApplicationData.Current.LocalSettings;
                    savedTheme = localSettings.Values["AppTheme"] as string;
                }
                
                if (!string.IsNullOrEmpty(savedTheme) && savedTheme != "default")
                {
                    // Theme will be applied after the window is created
                    // Store the theme preference for later application
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize theme: {ex.Message}");
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainWindow = _window as MainWindow;
            _window.Activate();
            
            // Show error dialog if service initialization failed
            if (_serviceInitializationFailed)
            {
                _ = ShowServiceInitializationErrorAsync();
            }
        }
        
        private async System.Threading.Tasks.Task ShowServiceInitializationErrorAsync()
        {
            // Wait a bit for the window to be fully loaded
            await System.Threading.Tasks.Task.Delay(500);
            
            var dialog = new ContentDialog()
             {
                 Title = GetLocalizedString("WiFiDirectUnavailable_Title", "WiFi Direct Unavailable"),
                 Content = string.IsNullOrWhiteSpace(_serviceInitializationError)
                    ? GetLocalizedString("WiFiDirectUnavailable_PeerDiscoveryMessage", ServiceManager.ServiceUnavailableMessage)
                    : _serviceInitializationError,
                 CloseButtonText = GetLocalizedString("Common_OK", "OK"),
                 XamlRoot = _window?.Content.XamlRoot
             };
            
            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to show error dialog: {ex.Message}");
            }
        }

        private static string GetLocalizedString(string resourceKey, string fallback)
        {
            try
            {
                var value = ResourceLoader.GetForViewIndependentUse().GetString(resourceKey);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }
        
    }
}
