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
        
        private bool _serviceInitializationFailed = false;
        private string _serviceInitializationError = string.Empty;
        
        public App()
        {
            InitializeComponent();
            
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

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
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
