using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WDCableWUI.Protocol;
using WDCableWUI.Services;

namespace WDCableWUI.UI.Audio;

public sealed partial class AudioPage : Page
{
    private AudioService? _audioService;
    private AudioService? _subscribedAudioService;
    private SessionManager? _sessionManager;
    private SessionManager? _subscribedSessionManager;
    private bool _controlsReady;
    private bool _restoringServiceState;

    public AudioPage()
    {
        InitializeComponent();
        _controlsReady = true;
        Unloaded += OnPageUnloaded;
        UpdateControlsForMode();
        SelectLatencyMode(_audioService?.LatencyMode ?? AudioProtocol.LatencyModeLow);
        SelectQualityMode(_audioService?.QualityMode ?? AudioProtocol.QualityStandard);
        StateText.Text = StateDisplayName(AudioService.StateIdle);
        StateDetailsText.Text = "Audio Link is idle";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            InitializeServices();
            SubscribeToServiceEvents();
            UpdateConnectionStatus();
            RestoreServiceSnapshot();
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioPage navigation failed: {ex}");
            ShowInfo("Audio page could not initialize. See debug output for details.", InfoBarSeverity.Error);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        UnsubscribeFromServiceEvents();
    }

    private void InitializeServices()
    {
        try
        {
            _audioService = ServiceManager.AreWiFiDirectServicesAvailable ? ServiceManager.AudioService : null;
            _sessionManager = ServiceManager.AreWiFiDirectServicesAvailable ? ServiceManager.SessionManager : null;
            SelectLatencyMode(_audioService?.LatencyMode ?? AudioProtocol.LatencyModeLow);
            SelectQualityMode(_audioService?.QualityMode ?? AudioProtocol.QualityStandard);
        }
        catch
        {
            _audioService = null;
            _sessionManager = null;
        }
    }

    private void SubscribeToServiceEvents()
    {
        if (_audioService != null && _subscribedAudioService != _audioService)
        {
            UnsubscribeFromAudioEvents();
            _audioService.StateChanged += OnAudioStateChanged;
            _audioService.StatsChanged += OnAudioStatsChanged;
            _audioService.ErrorOccurred += OnAudioErrorOccurred;
            _audioService.StatusChanged += OnAudioStatusChanged;
            _subscribedAudioService = _audioService;
        }

        if (_sessionManager != null && _subscribedSessionManager != _sessionManager)
        {
            UnsubscribeFromSessionEvents();
            _sessionManager.StateChanged += OnSessionStateChanged;
            _sessionManager.SessionReady += OnSessionReady;
            _sessionManager.SessionFailed += OnSessionFailed;
            _subscribedSessionManager = _sessionManager;
        }
    }

    private void UnsubscribeFromServiceEvents()
    {
        UnsubscribeFromAudioEvents();
        UnsubscribeFromSessionEvents();
    }

    private void UnsubscribeFromAudioEvents()
    {
        if (_subscribedAudioService == null)
        {
            return;
        }

        _subscribedAudioService.StateChanged -= OnAudioStateChanged;
        _subscribedAudioService.StatsChanged -= OnAudioStatsChanged;
        _subscribedAudioService.ErrorOccurred -= OnAudioErrorOccurred;
        _subscribedAudioService.StatusChanged -= OnAudioStatusChanged;
        _subscribedAudioService = null;
    }

    private void UnsubscribeFromSessionEvents()
    {
        if (_subscribedSessionManager == null)
        {
            return;
        }

        _subscribedSessionManager.StateChanged -= OnSessionStateChanged;
        _subscribedSessionManager.SessionReady -= OnSessionReady;
        _subscribedSessionManager.SessionFailed -= OnSessionFailed;
        _subscribedSessionManager = null;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromServiceEvents();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audioService == null)
        {
            ShowInfo("Audio service is not available.", InfoBarSeverity.Error);
            return;
        }

        StartButton.IsEnabled = false;
        try
        {
            var mode = SelectedMode();
            if (mode == AudioService.ModeReceive)
            {
                await _audioService.StartReceiveAsync();
            }
            else
            {
                _audioService.SetLatencyMode(SelectedLatencyMode());
                _audioService.SetQualityMode(SelectedQualityMode());
                await _audioService.StartSendAsync();
            }
        }
        catch (Exception ex)
        {
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            UpdateButtonStates();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audioService == null)
        {
            return;
        }

        StopButton.IsEnabled = false;
        try
        {
            await _audioService.StopAsync();
        }
        catch (Exception ex)
        {
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            UpdateButtonStates();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            InitializeServices();
            SubscribeToServiceEvents();
            UpdateConnectionStatus();
            RestoreServiceSnapshot();
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioPage refresh failed: {ex}");
            ShowInfo("Audio page refresh failed. See debug output for details.", InfoBarSeverity.Error);
        }
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_controlsReady || _restoringServiceState)
        {
            return;
        }

        UpdateControlsForMode();
        UpdateButtonStates();
    }

    private void LatencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_controlsReady || _restoringServiceState)
        {
            return;
        }

        if (SelectedMode() == AudioService.ModeSend)
        {
            _audioService?.SetLatencyMode(SelectedLatencyMode());
        }
        UpdateButtonStates();
    }

    private void EncodingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_controlsReady || _restoringServiceState)
        {
            return;
        }

        if (SelectedMode() == AudioService.ModeSend)
        {
            _audioService?.SetQualityMode(SelectedQualityMode());
        }

        UpdateButtonStates();
    }

    private void OnAudioStateChanged(object? sender, AudioStateChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyAudioState(e, showInfo: true);
            UpdateButtonStates();
        });
    }

    private void OnAudioStatsChanged(object? sender, AudioStatsEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyAudioStats(e);
        });
    }

    private void OnAudioErrorOccurred(object? sender, AudioErrorEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var displayMessage = $"{e.Code}: {e.Message}";
            ShowInfo(displayMessage, InfoBarSeverity.Error);
            StateDetailsText.Text = displayMessage;
            UpdateButtonStates();
        });
    }

    private void OnAudioStatusChanged(object? sender, string status)
    {
        DispatcherQueue.TryEnqueue(() => StateDetailsText.Text = status);
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateConnectionStatus();
            UpdateButtonStates();
        });
    }

    private void OnSessionReady(object? sender, SessionReadyEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateConnectionStatus();
            UpdateButtonStates();
        });
    }

    private void OnSessionFailed(object? sender, SessionFailedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowInfo(e.Message, InfoBarSeverity.Error);
            UpdateConnectionStatus();
            UpdateButtonStates();
        });
    }

    private void UpdateConnectionStatus()
    {
        if (!_controlsReady)
        {
            return;
        }

        if (!ServiceManager.AreWiFiDirectServicesAvailable)
        {
            ShowInfo(ServiceManager.ServiceUnavailableMessage, InfoBarSeverity.Warning);
            StateText.Text = "Unavailable";
            StateDetailsText.Text = ServiceManager.ServiceUnavailableMessage;
            UpdateStateIcon(AudioService.StateIdle);
            return;
        }

        if (_sessionManager?.IsReady == true)
        {
            var peerCaps = _sessionManager.PeerCapabilities;
            if (AudioProtocol.PeerSupportsAudio(peerCaps))
            {
                ShowInfo("Audio Link is available.", InfoBarSeverity.Success);
                StateDetailsText.Text = _audioService?.IsActive == true ? StateDetailsText.Text : "Audio Link is available";
            }
            else
            {
                ShowInfo("The connected peer does not advertise Audio Link.", InfoBarSeverity.Warning);
                StateDetailsText.Text = "The peer does not support Audio Link";
            }
        }
        else if (ServiceManager.IsConnected)
        {
            ShowInfo("Waiting for WDCable session readiness.", InfoBarSeverity.Warning);
            StateDetailsText.Text = "Waiting for WDCable session readiness";
        }
        else
        {
            ShowInfo("Connect a peer to use Audio Link.", InfoBarSeverity.Warning);
            StateDetailsText.Text = "No device connected";
        }
    }

    private void UpdateButtonStates()
    {
        if (!_controlsReady)
        {
            return;
        }

        var ready = _sessionManager?.IsReady == true;
        var peerSupportsAudio = ready && AudioProtocol.PeerSupportsAudio(_sessionManager?.PeerCapabilities ?? []);
        var active = _audioService?.IsActive == true;
        StartButton.IsEnabled = peerSupportsAudio && !active;
        StopButton.IsEnabled = active;
        ModeComboBox.IsEnabled = !active;
        var isSendMode = SelectedMode() == AudioService.ModeSend;
        SourceComboBox.IsEnabled = !active && isSendMode;
        LatencyComboBox.IsEnabled = !active && isSendMode;
        EncodingComboBox.IsEnabled = !active && isSendMode;
    }

    private void UpdateControlsForMode()
    {
        if (!_controlsReady)
        {
            return;
        }

        var mode = SelectedMode();
        var isSendMode = mode == AudioService.ModeSend;
        if (isSendMode)
        {
            SourceComboBox.SelectedIndex = 0;
        }
        else
        {
            SourceComboBox.SelectedIndex = 1;
        }

        var senderVisibility = isSendMode ? Visibility.Visible : Visibility.Collapsed;
        LatencyComboBox.Visibility = senderVisibility;
        EncodingComboBox.Visibility = senderVisibility;
    }

    private string SelectedMode()
    {
        return (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == AudioService.ModeReceive
            ? AudioService.ModeReceive
            : AudioService.ModeSend;
    }

    private string SelectedLatencyMode()
    {
        return (LatencyComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == AudioProtocol.LatencyModeStable
            ? AudioProtocol.LatencyModeStable
            : AudioProtocol.LatencyModeLow;
    }

    private string SelectedQualityMode()
    {
        return AudioProtocol.NormalizeQualityMode((EncodingComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
    }

    private void SelectLatencyMode(string latencyMode)
    {
        if (!_controlsReady)
        {
            return;
        }

        LatencyComboBox.SelectedIndex = AudioProtocol.NormalizeLatencyMode(latencyMode) == AudioProtocol.LatencyModeStable
            ? 1
            : 0;
    }

    private void SelectQualityMode(string qualityMode)
    {
        if (!_controlsReady)
        {
            return;
        }

        EncodingComboBox.SelectedIndex = AudioProtocol.NormalizeQualityMode(qualityMode) switch
        {
            AudioProtocol.QualityBalanced => 1,
            AudioProtocol.QualityHigh => 2,
            AudioProtocol.QualityNearLossless => 3,
            _ => 0
        };
    }

    private void RestoreServiceSnapshot()
    {
        var snapshot = _audioService?.GetSnapshot();
        if (snapshot == null)
        {
            return;
        }

        ApplyAudioState(snapshot.State, showInfo: snapshot.State.State != AudioService.StateIdle);
        if (snapshot.LatestStats != null)
        {
            ApplyAudioStats(snapshot.LatestStats);
        }
    }

    private void ApplyAudioState(AudioStateChangedEventArgs state, bool showInfo)
    {
        _restoringServiceState = true;
        try
        {
            if (state.Mode == AudioService.ModeSend)
            {
                ModeComboBox.SelectedIndex = 0;
            }
            else if (state.Mode == AudioService.ModeReceive)
            {
                ModeComboBox.SelectedIndex = 1;
            }

            SelectLatencyMode(state.LatencyMode);
            SelectQualityMode(state.QualityMode);
            UpdateControlsForMode();
        }
        finally
        {
            _restoringServiceState = false;
        }

        StateText.Text = StateDisplayName(state.State);
        if (state.State != AudioService.StateIdle ||
            !string.Equals(state.Message, "Audio Link is idle", StringComparison.Ordinal))
        {
            StateDetailsText.Text = state.Message;
        }
        UpdateStateIcon(state.State);
        if (showInfo)
        {
            ShowInfo(
                state.Message,
                state.State == AudioService.StateStreaming
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Informational);
        }
    }

    private void ApplyAudioStats(AudioStatsEventArgs stats)
    {
        _restoringServiceState = true;
        try
        {
            SelectLatencyMode(stats.LatencyMode);
            SelectQualityMode(stats.QualityMode);
        }
        finally
        {
            _restoringServiceState = false;
        }
        LatencyText.Text = DisplayLatencyMode(stats.LatencyMode);
        QualityText.Text = DisplayQualityMode(stats.QualityMode);
        ConfiguredBitrateText.Text = FormatBitrate(stats.ConfiguredBitrateBps);
        BitrateText.Text = FormatBitrate(stats.BitrateBps);
        BufferText.Text = $"{stats.BufferLevelMs} ms";
        FramesSentText.Text = stats.FramesSent.ToString();
        FramesReceivedText.Text = stats.FramesReceived.ToString();
        DroppedText.Text = stats.DroppedFrames.ToString();
        UnderflowText.Text = stats.UnderflowCount.ToString();
        PacketLossText.Text = stats.PacketLossCount.ToString();
        LateDropsText.Text = stats.LatePacketDrops.ToString();
        OverflowDropsText.Text = stats.OverflowDrops.ToString();
        PlcText.Text = stats.PlcCount.ToString();
        RtcpLossText.Text = FormatFractionLost(stats.RtcpFractionLost);
        RtcpJitterText.Text = FormatRtpJitter(stats.RtcpJitter == 0 ? stats.LocalJitter : stats.RtcpJitter);
        RttText.Text = stats.LatencyMs >= 0 ? $"{stats.LatencyMs} ms" : "-";
    }

    private void UpdateStateIcon(string state)
    {
        if (state == AudioService.StateStreaming)
        {
            StateIcon.Glyph = "\uE8FB";
            StateIcon.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
        else if (state == AudioService.StateIdle)
        {
            StateIcon.Glyph = "\uE783";
            StateIcon.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }
        else
        {
            StateIcon.Glyph = "\uE9D9";
            StateIcon.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        if (!_controlsReady)
        {
            return;
        }

        AudioInfoBar.Message = message;
        AudioInfoBar.Severity = severity;
        AudioInfoBar.IsOpen = true;
    }

    private static string StateDisplayName(string state)
    {
        return state switch
        {
            AudioService.StateReceiveReady => "Receive Ready",
            AudioService.StateOfferSent => "Offer Sent",
            AudioService.StateConnecting => "Connecting",
            AudioService.StateStreaming => "Streaming",
            _ => "Idle"
        };
    }

    private static string FormatBitrate(long bitrateBps)
    {
        return bitrateBps >= 1000
            ? $"{bitrateBps / 1000.0:F1} kbps"
            : $"{bitrateBps} bps";
    }

    private static string DisplayLatencyMode(string latencyMode)
    {
        return AudioProtocol.NormalizeLatencyMode(latencyMode) == AudioProtocol.LatencyModeStable
            ? "Stable"
            : "Low latency";
    }

    private static string DisplayQualityMode(string qualityMode)
    {
        return AudioProtocol.NormalizeQualityMode(qualityMode) switch
        {
            AudioProtocol.QualityBalanced => "Balanced 64 kbps",
            AudioProtocol.QualityHigh => "High 128 kbps",
            AudioProtocol.QualityNearLossless => "Near lossless 256 kbps",
            _ => "Standard 32 kbps"
        };
    }

    private static string FormatFractionLost(byte fractionLost)
    {
        return $"{fractionLost * 100.0 / 256.0:F1}%";
    }

    private static string FormatRtpJitter(uint jitter)
    {
        return $"{jitter * 1000.0 / AudioProtocol.RtpClockRate:F1} ms";
    }
}
