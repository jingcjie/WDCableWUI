using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;
using Microsoft.UI.Dispatching;

namespace WDCableWUI.Services
{
    /// <summary>
    /// Singleton data manager service that handles all application data persistence.
    /// This service manages app settings, user preferences, and configuration data.
    /// </summary>
    public class DataManager
    {
        private static DataManager? _instance;
        private static readonly object _lock = new object();
        
        private readonly ApplicationDataContainer _localSettings;
        private readonly DispatcherQueue _dispatcherQueue;
        private bool _isDisposed = false;
        
        // Events
        public event EventHandler<string>? SettingChanged;
        public event EventHandler<string>? ErrorOccurred;
        
        /// <summary>
        /// Gets the singleton DataManager instance.
        /// </summary>
        public static DataManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new DataManager();
                        }
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Private constructor to enforce singleton pattern.
        /// </summary>
        private DataManager()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _localSettings = ApplicationData.Current.LocalSettings;
            
            // Initialize default settings if they don't exist
            InitializeDefaultSettings();
        }
        
        /// <summary>
        /// Initializes default application settings.
        /// </summary>
        private void InitializeDefaultSettings()
        {
            try
            {
                // Set default language if not exists
                if (!_localSettings.Values.ContainsKey("AppLanguage"))
                {
                    _localSettings.Values["AppLanguage"] = "system";
                }
                
                // Set default theme if not exists
                if (!_localSettings.Values.ContainsKey("AppTheme"))
                {
                    _localSettings.Values["AppTheme"] = "default";
                }
                
                // Set default download path if not exists
                if (!_localSettings.Values.ContainsKey("DownloadPath"))
                {
                    var defaultDownloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    _localSettings.Values["DownloadPath"] = defaultDownloadPath;
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to initialize default settings: {ex.Message}");
            }
        }
        
        #region Settings Management
        
        /// <summary>
        /// Gets the application language setting.
        /// </summary>
        /// <returns>The language tag (e.g., "en", "zh-CN", "system")</returns>
        public string GetAppLanguage()
        {
            try
            {
                return _localSettings.Values["AppLanguage"] as string ?? "system";
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to get app language: {ex.Message}");
                return "system";
            }
        }
        
        /// <summary>
        /// Sets the application language setting.
        /// </summary>
        /// <param name="languageTag">The language tag to set</param>
        public void SetAppLanguage(string languageTag)
        {
            try
            {
                _localSettings.Values["AppLanguage"] = languageTag;
                OnSettingChanged("AppLanguage");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to set app language: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets the application theme setting.
        /// </summary>
        /// <returns>The theme tag ("default", "light", "dark")</returns>
        public string GetAppTheme()
        {
            try
            {
                return _localSettings.Values["AppTheme"] as string ?? "default";
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to get app theme: {ex.Message}");
                return "default";
            }
        }
        
        /// <summary>
        /// Sets the application theme setting.
        /// </summary>
        /// <param name="themeTag">The theme tag to set</param>
        public void SetAppTheme(string themeTag)
        {
            try
            {
                _localSettings.Values["AppTheme"] = themeTag;
                OnSettingChanged("AppTheme");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to set app theme: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets the download path setting.
        /// </summary>
        /// <returns>The download path</returns>
        public string GetDownloadPath()
        {
            try
            {
                var path = _localSettings.Values["DownloadPath"] as string;
                if (string.IsNullOrEmpty(path))
                {
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    SetDownloadPath(path);
                }
                return path;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to get download path: {ex.Message}");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }
        
        /// <summary>
        /// Sets the download path setting.
        /// </summary>
        /// <param name="path">The download path to set</param>
        public void SetDownloadPath(string path)
        {
            try
            {
                _localSettings.Values["DownloadPath"] = path;
                OnSettingChanged("DownloadPath");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to set download path: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets a generic setting value.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <param name="defaultValue">The default value if the setting doesn't exist</param>
        /// <returns>The setting value or default value</returns>
        public T GetSetting<T>(string key, T defaultValue)
        {
            try
            {
                var value = _localSettings.Values[key];
                if (value != null && value is T typedValue)
                {
                    return typedValue;
                }
                return defaultValue;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to get setting '{key}': {ex.Message}");
                return defaultValue;
            }
        }
        
        /// <summary>
        /// Sets a generic setting value.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <param name="value">The setting value</param>
        public void SetSetting<T>(string key, T value)
        {
            try
            {
                _localSettings.Values[key] = value;
                OnSettingChanged(key);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to set setting '{key}': {ex.Message}");
            }
        }
        
        /// <summary>
        /// Removes a setting.
        /// </summary>
        /// <param name="key">The setting key to remove</param>
        public void RemoveSetting(string key)
        {
            try
            {
                if (_localSettings.Values.ContainsKey(key))
                {
                    _localSettings.Values.Remove(key);
                    OnSettingChanged(key);
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to remove setting '{key}': {ex.Message}");
            }
        }
        
        /// <summary>
        /// Checks if a setting exists.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <returns>True if the setting exists, false otherwise</returns>
        public bool HasSetting(string key)
        {
            try
            {
                return _localSettings.Values.ContainsKey(key);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to check setting '{key}': {ex.Message}");
                return false;
            }
        }
        
        #endregion
        
        #region Event Handlers
        
        /// <summary>
        /// Raises the SettingChanged event.
        /// </summary>
        /// <param name="settingKey">The key of the setting that changed</param>
        private void OnSettingChanged(string settingKey)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => SettingChanged?.Invoke(this, settingKey));
            }
            catch
            {
                // Ignore dispatcher errors during event delivery
            }
        }
        
        /// <summary>
        /// Raises the ErrorOccurred event.
        /// </summary>
        /// <param name="error">The error message</param>
        private void OnErrorOccurred(string error)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => ErrorOccurred?.Invoke(this, error));
            }
            catch
            {
                // Ignore dispatcher errors during error reporting
            }
        }
        
        #endregion
        
        #region Chat History Management
        
        /// <summary>
        /// Saves chat history to persistent storage.
        /// </summary>
        /// <param name="messages">The list of chat messages to save</param>
        public void SaveChatHistory(IEnumerable<ChatMessageData> messages)
        {
            try
            {
                var json = JsonSerializer.Serialize(messages.ToList(), new JsonSerializerOptions { WriteIndented = true });
                _localSettings.Values["ChatHistory"] = json;
                OnSettingChanged("ChatHistory");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to save chat history: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Loads chat history from persistent storage.
        /// </summary>
        /// <returns>The list of saved chat messages</returns>
        public List<ChatMessageData> LoadChatHistory()
        {
            try
            {
                var json = _localSettings.Values["ChatHistory"] as string;
                if (string.IsNullOrEmpty(json))
                {
                    return new List<ChatMessageData>();
                }
                
                var messages = JsonSerializer.Deserialize<List<ChatMessageData>>(json);
                return messages ?? new List<ChatMessageData>();
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to load chat history: {ex.Message}");
                return new List<ChatMessageData>();
            }
        }
        
        /// <summary>
        /// Clears all saved chat history.
        /// </summary>
        public void ClearChatHistory()
        {
            try
            {
                if (_localSettings.Values.ContainsKey("ChatHistory"))
                {
                    _localSettings.Values.Remove("ChatHistory");
                    OnSettingChanged("ChatHistory");
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to clear chat history: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Speed Test Records Management
        
        /// <summary>
        /// Saves speed test records to persistent storage.
        /// </summary>
        /// <param name="records">The list of speed test records to save</param>
        public void SaveSpeedTestRecords(IEnumerable<SpeedTestRecordData> records)
        {
            try
            {
                var json = JsonSerializer.Serialize(records.ToList(), new JsonSerializerOptions { WriteIndented = true });
                _localSettings.Values["SpeedTestRecords"] = json;
                OnSettingChanged("SpeedTestRecords");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to save speed test records: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Loads speed test records from persistent storage.
        /// </summary>
        /// <returns>The list of saved speed test records</returns>
        public List<SpeedTestRecordData> LoadSpeedTestRecords()
        {
            try
            {
                var json = _localSettings.Values["SpeedTestRecords"] as string;
                if (string.IsNullOrEmpty(json))
                {
                    return new List<SpeedTestRecordData>();
                }
                
                var records = JsonSerializer.Deserialize<List<SpeedTestRecordData>>(json);
                return records ?? new List<SpeedTestRecordData>();
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to load speed test records: {ex.Message}");
                return new List<SpeedTestRecordData>();
            }
        }
        
        /// <summary>
        /// Clears all saved speed test records.
        /// </summary>
        public void ClearSpeedTestRecords()
        {
            try
            {
                if (_localSettings.Values.ContainsKey("SpeedTestRecords"))
                {
                    _localSettings.Values.Remove("SpeedTestRecords");
                    OnSettingChanged("SpeedTestRecords");
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to clear speed test records: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Singleton Management
        
        /// <summary>
        /// Resets the singleton instance. Used for testing and cleanup.
        /// </summary>
        public static void ResetInstance()
        {
            lock (_lock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }
        
        /// <summary>
        /// Disposes the DataManager and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            
            try
            {
                // Clear events
                SettingChanged = null;
                ErrorOccurred = null;
                
                _isDisposed = true;
            }
            catch
            {
                // Ignore errors during disposal
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// Data structure for persisting chat messages.
    /// </summary>
    public class ChatMessageData
    {
        public int Type { get; set; } // 0 = Self, 1 = Peer, 2 = System
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
    
    /// <summary>
    /// Data structure for persisting speed test records.
    /// </summary>
    public class SpeedTestRecordData
    {
        public int TestType { get; set; } // 0 = Upload, 1 = Download
        public long DataSize { get; set; }
        public double DurationMs { get; set; }
        public double SpeedMbps { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }
}