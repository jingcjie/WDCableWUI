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
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WDCableWUI.Services;
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
            
            // Initialize localization
            InitializeLocalization();
            
            // Initialize theme
            InitializeTheme();
            
            // Initialize ServiceManager during app startup with proper error handling
            try
            {
                ServiceManager.Initialize();
            }
            catch (NotSupportedException ex)
            {
                // WiFi Direct not supported - show user-friendly message
                System.Diagnostics.Debug.WriteLine($"WiFi Direct not supported: {ex.Message}");
                _serviceInitializationFailed = true;
                _serviceInitializationError = "WiFi Direct is not supported in this system, the windows version can be too low or no wireless card is installed correctly";
                // Log the error but continue app startup
                // Services will be null but the app should still function
            }
            catch (Exception ex)
            {
                // Handle other initialization errors gracefully - don't crash the app
                System.Diagnostics.Debug.WriteLine($"Failed to initialize ServiceManager: {ex.Message}");
                _serviceInitializationFailed = true;
                _serviceInitializationError = "WiFi Direct is not supported in this system, the windows version can be too low or no wireless card is installed correctly";
                // Log the error but continue app startup
                // Services will be null but the app should still function
            }
        }

        private void InitializeLocalization()
        {
            try
            {
                string savedLanguage = null;
                
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
                string savedTheme = null;
                
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
                 Title = "WiFi Direct Not Supported",
                 Content = "WiFi Direct is not supported in this system, the windows version can be too low or no wireless card is installed correctly.",
                 CloseButtonText = "OK",
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
        
        /// <summary>
        /// Cleanup when application is closing.
        /// </summary>
        ~App()
        {
            ServiceManager.Shutdown();
        }
    }
}
