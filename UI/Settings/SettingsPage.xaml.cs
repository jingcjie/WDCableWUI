using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Globalization;
using Windows.Globalization;
using Windows.Storage;
using Windows.System;
using WDCableWUI.Services;

namespace WDCableWUI.UI.Settings
{
    public sealed partial class SettingsPage : Page
    {
        private DataManager? _dataManager;
        private bool _isInitializing = true;

        public SettingsPage()
        {
            this.InitializeComponent();
            InitializeDataManager();
            
            LoadSettings();
            _isInitializing = false;
        }
        
        private void InitializeDataManager()
        {
            try
            {
                _dataManager = ServiceManager.IsInitialized ? ServiceManager.DataManager : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DataManager not available: {ex.Message}");
                _dataManager = null;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadSettings();
        }

        private void LoadSettings()
        {
            string savedLanguage;
            string savedTheme;
            
            if (_dataManager == null)
            {
                // Fallback to direct access if DataManager is not available
                var localSettings = ApplicationData.Current.LocalSettings;
                savedLanguage = localSettings.Values["AppLanguage"] as string ?? "system";
                savedTheme = localSettings.Values["AppTheme"] as string ?? "default";
            }
            else
            {
                // Load language setting
                savedLanguage = _dataManager.GetAppLanguage();
                // Load theme setting
                savedTheme = _dataManager.GetAppTheme();
            }
            
            SelectLanguageItem(savedLanguage);
            SelectThemeItem(savedTheme);
            
            // Apply the current theme to ensure it's active
            ApplyTheme(savedTheme);
        }

        private void SelectLanguageItem(string languageTag)
        {
            switch (languageTag)
            {
                case "system":
                    LanguageComboBox.SelectedItem = FollowSystemItem;
                    break;
                case "en":
                    LanguageComboBox.SelectedItem = EnglishItem;
                    break;
                case "zh-CN":
                    LanguageComboBox.SelectedItem = ChineseItem;
                    break;
                default:
                    LanguageComboBox.SelectedItem = FollowSystemItem;
                    break;
            }
        }

        private void SelectThemeItem(string themeTag)
        {
            switch (themeTag)
            {
                case "default":
                    ThemeComboBox.SelectedItem = DefaultThemeItem;
                    break;
                case "light":
                    ThemeComboBox.SelectedItem = LightThemeItem;
                    break;
                case "dark":
                    ThemeComboBox.SelectedItem = DarkThemeItem;
                    break;
                default:
                    ThemeComboBox.SelectedItem = DefaultThemeItem;
                    break;
            }
        }

        private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Don't trigger during initialization
            if (_isInitializing) return;
            
            if (LanguageComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var languageTag = selectedItem.Tag as string;
                if (languageTag != null)
                {
                    // Save the language preference
                    if (_dataManager != null)
                    {
                        _dataManager.SetAppLanguage(languageTag);
                    }
                    else
                    {
                        // Fallback to direct access
                        var localSettings = ApplicationData.Current.LocalSettings;
                        localSettings.Values["AppLanguage"] = languageTag;
                    }

                    // Apply the language change
                    await ApplyLanguageAsync(languageTag);
                }
            }
        }

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Don't trigger during initialization
            if (_isInitializing) return;
            
            if (ThemeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var themeTag = selectedItem.Tag as string;
                if (themeTag != null)
                {
                    // Save the theme preference
                    if (_dataManager != null)
                    {
                        _dataManager.SetAppTheme(themeTag);
                    }
                    else
                    {
                        // Fallback to direct access
                        var localSettings = ApplicationData.Current.LocalSettings;
                        localSettings.Values["AppTheme"] = themeTag;
                    }

                    // Apply the theme change
                    ApplyTheme(themeTag);
                }
            }
        }

        private async System.Threading.Tasks.Task ApplyLanguageAsync(string languageTag)
        {
            try
            {
                // Debug: Verify the language is saved
                System.Diagnostics.Debug.WriteLine($"Applying language: {languageTag}");
                var savedLanguage = _dataManager?.GetAppLanguage() ?? "unknown";
                System.Diagnostics.Debug.WriteLine($"Saved language in settings: {savedLanguage}");
                
                // Show restart dialog for language change
                var dialog = new ContentDialog()
                {
                    Title = "Language Changed",
                    Content = "The language change will take effect after restarting the application. Would you like to restart now?",
                    PrimaryButtonText = "Restart",
                    SecondaryButtonText = "Later",
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    try
                    {
                        // Ensure settings are flushed to storage
                        System.Diagnostics.Debug.WriteLine("Restarting application...");
                        
                        // Use the standard restart method
                        Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
                    }
                    catch (Exception restartEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Restart failed: {restartEx.Message}");
                        
                        // Show manual restart message
                        var manualDialog = new ContentDialog()
                        {
                            Title = "Manual Restart Required",
                            Content = "Please manually restart the application for the language change to take effect.",
                            CloseButtonText = "OK",
                            XamlRoot = this.XamlRoot
                        };
                        await manualDialog.ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying language: {ex.Message}");
            }
        }

        private void ApplyTheme(string themeTag)
        {
            try
            {
                var window = App.MainWindow;
                if (window?.Content is FrameworkElement rootElement)
                {
                    switch (themeTag)
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
                System.Diagnostics.Debug.WriteLine($"Error applying theme: {ex.Message}");
            }
        }

        private async void WindowsRepoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Launcher.LaunchUriAsync(new Uri("https://github.com/jingcjie/WDCableWUI"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening Windows repository: {ex.Message}");
            }
        }

        private async void AndroidRepoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Launcher.LaunchUriAsync(new Uri("https://github.com/jingcjie/WDCable_flutter"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening Android repository: {ex.Message}");
            }
        }

    }
}