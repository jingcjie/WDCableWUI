using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

public sealed class AudioService : IDisposable
{
    public const string ModeIdle = "idle";
    public const string ModeSend = "send";
    public const string ModeReceive = "receive";

    public const string StateIdle = "idle";
    public const string StateReceiveReady = "receiveReady";
    public const string StateOfferSent = "offerSent";
    public const string StateConnecting = "connecting";
    public const string StateStreaming = "streaming";

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static AudioService? _instance;
    private static readonly object InstanceLock = new();

    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly object _stateLock = new();
    private readonly object _udpLock = new();
    private readonly object _rtpStatsLock = new();
    private SessionManager? _sessionManager;
    private CancellationTokenSource? _streamCancellationTokenSource;
    private WasapiLoopbackCapture? _loopbackCapture;
    private UdpClient? _rtpClient;
    private UdpClient? _rtcpClient;
    private IPEndPoint? _rtpDestination;
    private IPEndPoint? _rtcpDestination;
    private JitterBuffer _jitterBuffer;
    private long _bytesSent;
    private long _bytesReceived;
    private long _packetsSent;
    private long _packetsReceived;
    private long _rtcpSendErrors;
    private long _udpSendErrors;
    private long _udpReceiveErrors;
    private long _encodeErrors;
    private long _decodeErrors;
    private long _playbackSilenceFillCount;
    private long _highestSequenceReceived = -1;
    private long _lastRtcpReceivedPackets;
    private long _lastRtcpLostPackets;
    private long _streamId;
    private string _mode = ModeIdle;
    private string _state = StateIdle;
    private string _offerId = "";
    private string _latencyMode;
    private string _qualityMode;
    private int _configuredBitrateBps;
    private bool _isSender;
    private bool _peerReady;
    private bool _captureStarted;
    private bool _playbackStarted;
    private bool _statsStarted;
    private bool _rtcpStarted;
    private bool _isDisposed;
    private ushort _rtpSequence;
    private uint _rtpTimestamp;
    private uint _localSsrc;
    private uint _peerSsrc;
    private uint _lastSenderReportCompact;
    private uint _lastSenderReportArrivalCompact;
    private byte _rtcpFractionLost;
    private uint _rtcpReportedJitter;
    private long _rtcpRttMs = -1;
    private double _interarrivalJitter;
    private long? _lastTransit;

    private AudioService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _latencyMode = LoadLatencyMode();
        _qualityMode = LoadQualityMode();
        _configuredBitrateBps = AudioProtocol.BitrateForQualityMode(_qualityMode);
        _jitterBuffer = new JitterBuffer(_latencyMode);
        _sessionManager = ServiceManager.SessionManager;
        if (_sessionManager != null)
        {
            SubscribeToSession(_sessionManager);
        }
    }

    public static AudioService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (InstanceLock)
                {
                    _instance ??= new AudioService();
                }
            }

            return _instance;
        }
    }

    public bool IsConnected => _sessionManager?.IsReady ?? false;

    public bool IsActive => _state != StateIdle;

    public string Mode => _mode;

    public string State => _state;

    public string LatencyMode => _latencyMode;

    public string QualityMode => _qualityMode;

    public int ConfiguredBitrateBps => _configuredBitrateBps;

    public event EventHandler<AudioStateChangedEventArgs>? StateChanged;
    public event EventHandler<AudioStatsEventArgs>? StatsChanged;
    public event EventHandler<AudioErrorEventArgs>? ErrorOccurred;
    public event EventHandler<string>? StatusChanged;

    public static void ResetInstance()
    {
        lock (InstanceLock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }

    public void SetLatencyMode(string latencyMode)
    {
        var normalized = AudioProtocol.NormalizeLatencyMode(latencyMode);
        _latencyMode = normalized;
        SaveLatencyMode(normalized);
        if (!IsActive)
        {
            _jitterBuffer = new JitterBuffer(normalized);
        }
    }

    public void SetQualityMode(string qualityMode)
    {
        var normalized = AudioProtocol.NormalizeQualityMode(qualityMode);
        _qualityMode = normalized;
        _configuredBitrateBps = AudioProtocol.BitrateForQualityMode(normalized);
        SaveQualityMode(normalized);
    }

    public async Task StartReceiveAsync(CancellationToken cancellationToken = default)
    {
        var sessionInfo = RequireAudioSession();
        if (!AudioProtocol.PeerSupportsAudio(sessionInfo.PeerCapabilities))
        {
            await EmitLocalStartErrorAsync(AudioProtocol.ErrorRtpUnsupported, "The connected peer does not advertise RTP/libopus Audio Link.").ConfigureAwait(false);
            return;
        }

        lock (_stateLock)
        {
            if (_state != StateIdle)
            {
                _ = SendAudioErrorAsync(_streamId, AudioProtocol.ErrorBusy, "Audio Link is already active.");
                EmitAudioError(AudioProtocol.ErrorBusy, "Audio Link is already active.", _streamId);
                return;
            }

            _localSsrc = AudioProtocol.NewSsrc();
            ResetStateLocked(ModeReceive, StateReceiveReady, streamId: 0, offerId: "", isSender: false);
        }

        ResetStats();
        try
        {
            await SendAudioControlAsync(AudioProtocol.ReceiveReady(0), cancellationToken).ConfigureAwait(false);
            EmitState("Ready to receive audio");
        }
        catch
        {
            CleanupLocal("receive_start_failed", emitStopped: true);
            throw;
        }
    }

    public async Task StartSendAsync(CancellationToken cancellationToken = default)
    {
        var sessionInfo = RequireAudioSession();
        if (!AudioProtocol.PeerSupportsAudio(sessionInfo.PeerCapabilities))
        {
            await EmitLocalStartErrorAsync(AudioProtocol.ErrorRtpUnsupported, "The connected peer does not advertise RTP/libopus Audio Link.").ConfigureAwait(false);
            return;
        }

        var selectedQualityMode = AudioProtocol.NormalizeQualityMode(_qualityMode);
        var configuredBitrateBps = AudioProtocol.BitrateForQualityMode(selectedQualityMode);
        var peerSupportsQualitySelection = AudioProtocol.PeerSupportsAudioQualitySelection(sessionInfo.PeerCapabilities);
        TraceAudio($"Send requested: latency={_latencyMode} quality={selectedQualityMode} configuredBitrateBps={configuredBitrateBps} peerQualitySelect={peerSupportsQualitySelection}");
        if (AudioProtocol.RequiresQualitySelectionCapability(selectedQualityMode) && !peerSupportsQualitySelection)
        {
            const string message = "The connected peer does not support sender audio quality selection.";
            TraceAudio($"Send rejected: {message} quality={selectedQualityMode}");
            await EmitLocalStartErrorAsync(AudioProtocol.ErrorRtpUnsupported, message).ConfigureAwait(false);
            return;
        }

        if (!CanCreateLoopbackCapture(out var captureError))
        {
            await EmitLocalStartErrorAsync(AudioProtocol.ErrorCaptureFailed, captureError).ConfigureAwait(false);
            return;
        }

        var streamId = BulkProtocol.NextStreamId();
        var offerId = Guid.NewGuid().ToString();
        lock (_stateLock)
        {
            if (_state != StateIdle)
            {
                _ = SendAudioErrorAsync(_streamId, AudioProtocol.ErrorBusy, "Audio Link is already active.");
                EmitAudioError(AudioProtocol.ErrorBusy, "Audio Link is already active.", _streamId);
                return;
            }

            _localSsrc = AudioProtocol.NewSsrc();
            _rtpSequence = unchecked((ushort)Random.Shared.Next(0, ushort.MaxValue + 1));
            _rtpTimestamp = unchecked((uint)Random.Shared.Next());
            _qualityMode = selectedQualityMode;
            _configuredBitrateBps = configuredBitrateBps;
            ResetStateLocked(ModeSend, StateOfferSent, streamId, offerId, isSender: true);
        }

        ResetStats();
        try
        {
            await SendAudioControlAsync(
                AudioProtocol.Offer(
                    streamId,
                    offerId,
                    AudioProtocol.SourceSystemAudio,
                    _localSsrc,
                    sessionInfo.TransportRole,
                    _latencyMode,
                    selectedQualityMode,
                    configuredBitrateBps),
                cancellationToken).ConfigureAwait(false);
            EmitState("System audio offer sent");
        }
        catch
        {
            CleanupLocal("send_start_failed", emitStopped: true);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var currentStreamId = Interlocked.Read(ref _streamId);
        var currentMode = _mode;
        try
        {
            if (currentStreamId != 0 && _state != StateIdle)
            {
                await SendAudioControlAsync(AudioProtocol.Stop(currentStreamId, "local_stop"), cancellationToken).ConfigureAwait(false);
            }

            if (currentMode == ModeReceive)
            {
                await SendAudioControlAsync(AudioProtocol.ReceiveStopped(currentStreamId), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CleanupLocal("stopped", emitStopped: true);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CleanupLocal("service_disposed", emitStopped: false);
        if (_sessionManager != null)
        {
            UnsubscribeFromSession(_sessionManager);
            _sessionManager = null;
        }
    }

    private void SubscribeToSession(SessionManager sessionManager)
    {
        sessionManager.ControlFrameReceived += OnControlFrameReceived;
        sessionManager.StateChanged += OnSessionStateChanged;
        sessionManager.SessionFailed += OnSessionFailed;
    }

    private void UnsubscribeFromSession(SessionManager sessionManager)
    {
        sessionManager.ControlFrameReceived -= OnControlFrameReceived;
        sessionManager.StateChanged -= OnSessionStateChanged;
        sessionManager.SessionFailed -= OnSessionFailed;
    }

    private void OnControlFrameReceived(object? sender, ProtocolFrameReceivedEventArgs e)
    {
        if (e.Frame.Type == ProtocolFrameType.Error)
        {
            HandleErrorFrame(e.Frame.MetadataJson, e.Frame.StreamId);
            return;
        }

        if (e.Frame.Type != ProtocolFrameType.ControlMessage)
        {
            return;
        }

        Dictionary<string, JsonElement> metadata;
        try
        {
            metadata = AudioProtocol.ParseMetadata(e.Frame.MetadataJson);
        }
        catch (JsonException)
        {
            return;
        }

        var kind = AudioProtocol.OptionalString(metadata, "kind");
        if (!kind.StartsWith("audio.", StringComparison.Ordinal))
        {
            return;
        }

        switch (kind)
        {
            case AudioProtocol.KindReceiveReady:
                _peerReady = true;
                EmitState("Peer is ready to receive audio");
                break;
            case AudioProtocol.KindReceiveStopped:
                _peerReady = false;
                EmitState("Peer stopped receiving audio");
                break;
            case AudioProtocol.KindOffer:
                _ = HandleOfferAsync(metadata);
                break;
            case AudioProtocol.KindAccept:
                _ = HandleAcceptAsync(metadata);
                break;
            case AudioProtocol.KindStop:
                CleanupLocal(AudioProtocol.OptionalString(metadata, "reason", "peer_stop"), emitStopped: true);
                break;
        }
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (e.Phase is SessionPhase.Disconnected or SessionPhase.Disconnecting or SessionPhase.Failed)
        {
            CleanupLocal(e.DisconnectReason ?? "session_ended", emitStopped: true);
        }
    }

    private void OnSessionFailed(object? sender, SessionFailedEventArgs e)
    {
        CleanupLocal(e.Reason, emitStopped: true);
    }

    private async Task HandleOfferAsync(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        AudioOffer offer;
        try
        {
            offer = AudioProtocol.ParseOffer(metadata);
        }
        catch (FormatException ex)
        {
            await SendAudioErrorAsync(0, AudioProtocol.ErrorRtpUnsupported, ex.Message).ConfigureAwait(false);
            return;
        }

        if (_state != StateReceiveReady || _mode != ModeReceive)
        {
            await SendAudioErrorAsync(offer.StreamId, AudioProtocol.ErrorReceiverNotReady, "Receiver has not started Audio Link receive mode.").ConfigureAwait(false);
            return;
        }

        if (!AudioProtocol.IsCompatibleOffer(offer))
        {
            TraceAudio($"Offer rejected: latency={offer.LatencyMode} quality={offer.QualityMode} bitrateBps={offer.BitrateBps}");
            await SendAudioErrorAsync(offer.StreamId, AudioProtocol.ErrorRtpUnsupported, "Unsupported RTP/libopus audio offer.").ConfigureAwait(false);
            return;
        }

        var sessionInfo = RequireAudioSession();
        lock (_stateLock)
        {
            _streamId = offer.StreamId;
            _offerId = offer.OfferId;
            _isSender = false;
            _peerSsrc = offer.RtpSsrc;
            _latencyMode = offer.LatencyMode;
            _qualityMode = offer.QualityMode;
            _configuredBitrateBps = offer.BitrateBps;
            _jitterBuffer = new JitterBuffer(offer.LatencyMode);
            _state = StateConnecting;
        }
        ResetStats();
        TraceAudio($"Offer accepted config: latency={offer.LatencyMode} quality={offer.QualityMode} configuredBitrateBps={offer.BitrateBps}");

        var streamToken = ReplaceStreamCancellation();
        try
        {
            await OpenUdpSocketsAsync(sessionInfo, streamToken).ConfigureAwait(false);
            StartCommonLoops(streamToken);
            StartPlaybackIfNeeded(streamToken);
            await SendAudioControlAsync(
                AudioProtocol.Accept(
                    offer.StreamId,
                    offer.OfferId,
                    _localSsrc,
                    sessionInfo.TransportRole,
                    receiverProbeRequired: AudioProtocol.ReceiverProbeRequired(sessionInfo.TransportRole),
                    offer.LatencyMode,
                    offer.QualityMode,
                    offer.BitrateBps)).ConfigureAwait(false);

            if (sessionInfo.TransportRole == SessionTransportRole.Connector)
            {
                _ = RunProbeLoopAsync(streamToken);
            }

            EmitState("Audio offer accepted");
        }
        catch (SocketException ex)
        {
            await FailAudioAsync(AudioProtocol.ErrorRtpBindFailed, $"Audio RTP/RTCP bind failed: {ex.Message}", offer.StreamId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FailAudioAsync(AudioProtocol.ErrorTransportFailed, $"Audio UDP setup failed: {ex.Message}", offer.StreamId).ConfigureAwait(false);
        }
    }

    private async Task HandleAcceptAsync(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        AudioAccept accept;
        try
        {
            accept = AudioProtocol.ParseAccept(metadata);
        }
        catch (FormatException ex)
        {
            await FailAudioAsync(AudioProtocol.ErrorRtpUnsupported, ex.Message, Interlocked.Read(ref _streamId)).ConfigureAwait(false);
            return;
        }

        if (!_isSender || accept.StreamId != Interlocked.Read(ref _streamId) || accept.OfferId != _offerId)
        {
            return;
        }

        if (!AudioProtocol.IsCompatibleAccept(accept))
        {
            TraceAudio($"Accept rejected: latency={accept.LatencyMode} quality={accept.QualityMode} bitrateBps={accept.BitrateBps}");
            await FailAudioAsync(AudioProtocol.ErrorRtpUnsupported, "Peer accepted unsupported RTP/libopus audio details.", accept.StreamId).ConfigureAwait(false);
            return;
        }

        if (accept.LatencyMode != _latencyMode ||
            accept.QualityMode != _qualityMode ||
            accept.BitrateBps != _configuredBitrateBps)
        {
            TraceAudio($"Accept changed stream config: offered latency={_latencyMode} quality={_qualityMode} bitrateBps={_configuredBitrateBps}; accepted latency={accept.LatencyMode} quality={accept.QualityMode} bitrateBps={accept.BitrateBps}");
            await FailAudioAsync(AudioProtocol.ErrorRtpUnsupported, "Peer accepted a different RTP/libopus stream configuration.", accept.StreamId).ConfigureAwait(false);
            return;
        }

        var sessionInfo = RequireAudioSession();
        lock (_stateLock)
        {
            _peerSsrc = accept.RtpSsrc;
            _state = StateConnecting;
        }
        TraceAudio($"Accept verified config: latency={accept.LatencyMode} quality={accept.QualityMode} configuredBitrateBps={accept.BitrateBps}");

        var streamToken = ReplaceStreamCancellation();
        try
        {
            await OpenUdpSocketsAsync(sessionInfo, streamToken).ConfigureAwait(false);
            StartCommonLoops(streamToken);
            TryStartCaptureIfReady(streamToken);
            if (sessionInfo.TransportRole == SessionTransportRole.Listener && accept.ReceiverProbeRequired)
            {
                _ = RunProbeTimeoutAsync(accept.StreamId, streamToken);
            }

            EmitState("Audio offer accepted by peer");
        }
        catch (SocketException ex)
        {
            await FailAudioAsync(AudioProtocol.ErrorRtpBindFailed, $"Audio RTP/RTCP bind failed: {ex.Message}", accept.StreamId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FailAudioAsync(AudioProtocol.ErrorTransportFailed, $"Audio UDP setup failed: {ex.Message}", accept.StreamId).ConfigureAwait(false);
        }
    }

    private async Task OpenUdpSocketsAsync(AudioSessionInfo sessionInfo, CancellationToken cancellationToken)
    {
        CloseUdpSockets();

        var localAddress = ParseLocalAddress(sessionInfo.LocalAddress);
        var remoteAddress = ParseRemoteAddress(sessionInfo.PeerAddress);
        var isListener = sessionInfo.TransportRole == SessionTransportRole.Listener;

        var rtpLocal = new IPEndPoint(localAddress, isListener ? AudioProtocol.RtpPort : 0);
        var rtcpLocal = new IPEndPoint(localAddress, isListener ? AudioProtocol.RtcpPort : 0);
        var rtpClient = new UdpClient(rtpLocal);
        var rtcpClient = new UdpClient(rtcpLocal);
        rtpClient.Client.ReceiveBufferSize = 256 * 1024;
        rtcpClient.Client.ReceiveBufferSize = 64 * 1024;

        lock (_udpLock)
        {
            _rtpClient = rtpClient;
            _rtcpClient = rtcpClient;
            if (isListener)
            {
                _rtpDestination = null;
                _rtcpDestination = null;
            }
            else
            {
                _rtpDestination = new IPEndPoint(remoteAddress, AudioProtocol.RtpPort);
                _rtcpDestination = new IPEndPoint(remoteAddress, AudioProtocol.RtcpPort);
            }
        }

        TraceAudio($"UDP audio open: role={sessionInfo.TransportRole.GetEventName()} rtpLocal={rtpClient.Client.LocalEndPoint} rtcpLocal={rtcpClient.Client.LocalEndPoint} rtpDest={_rtpDestination} rtcpDest={_rtcpDestination} latency={_latencyMode} quality={_qualityMode} configuredBitrateBps={_configuredBitrateBps}");
        _ = RunRtpReceiveLoopAsync(rtpClient, cancellationToken);
        _ = RunRtcpReceiveLoopAsync(rtcpClient, cancellationToken);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RunProbeLoopAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 25 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            UdpClient? rtpClient;
            UdpClient? rtcpClient;
            IPEndPoint? rtpDestination;
            IPEndPoint? rtcpDestination;
            lock (_udpLock)
            {
                rtpClient = _rtpClient;
                rtcpClient = _rtcpClient;
                rtpDestination = _rtpDestination;
                rtcpDestination = _rtcpDestination;
            }

            if (rtpClient == null || rtcpClient == null || rtpDestination == null || rtcpDestination == null)
            {
                return;
            }

            await SendUdpAsync(rtpClient, AudioProtocol.RtpProbePayload, rtpDestination, cancellationToken).ConfigureAwait(false);
            await SendUdpAsync(rtcpClient, AudioProtocol.RtcpProbePayload, rtcpDestination, cancellationToken).ConfigureAwait(false);
            await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunProbeTimeoutAsync(long streamId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || !_isSender || _state == StateStreaming)
            {
                return;
            }

            IPEndPoint? rtpDestination;
            lock (_udpLock)
            {
                rtpDestination = _rtpDestination;
            }

            if (rtpDestination == null)
            {
                await FailAudioAsync(AudioProtocol.ErrorRtpProbeTimeout, "Timed out waiting for receiver UDP path probe.", streamId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunRtpReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.Buffer.AsSpan().SequenceEqual(AudioProtocol.RtpProbePayload))
                {
                    HandlePathProbe(isRtp: true, result.RemoteEndPoint);
                    continue;
                }

                if (_isSender)
                {
                    continue;
                }

                if (!RtpPacket.TryDecode(result.Buffer, out var packet) ||
                    packet.PayloadType != AudioProtocol.RtpPayloadType ||
                    packet.Ssrc != _peerSsrc)
                {
                    continue;
                }

                TrackRtpArrival(packet);
                _jitterBuffer.Add(new RtpAudioFrame(
                    packet.SequenceNumber,
                    packet.Timestamp,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    packet.Payload));
                Interlocked.Add(ref _bytesReceived, packet.Payload.Length);
                Interlocked.Increment(ref _packetsReceived);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _udpReceiveErrors);
                await FailAudioAsync(AudioProtocol.ErrorRtpReceiveFailed, $"RTP receive failed: {ex.Message}", Interlocked.Read(ref _streamId)).ConfigureAwait(false);
            }
        }
    }

    private async Task RunRtcpReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.Buffer.AsSpan().SequenceEqual(AudioProtocol.RtcpProbePayload))
                {
                    HandlePathProbe(isRtp: false, result.RemoteEndPoint);
                    continue;
                }

                if (!RtcpProtocol.TryDecode(result.Buffer, out var packet) || packet == null)
                {
                    TraceAudio($"RTCP parse skipped from {result.RemoteEndPoint}");
                    continue;
                }

                if (!_isSender && _rtcpDestination == null)
                {
                    lock (_udpLock)
                    {
                        _rtcpDestination ??= result.RemoteEndPoint;
                    }
                }

                HandleRtcpPacket(packet);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _udpReceiveErrors);
                TraceAudio($"RTCP receive failed: {ex.Message}");
            }
        }
    }

    private void HandlePathProbe(bool isRtp, IPEndPoint remoteEndPoint)
    {
        if (!_isSender)
        {
            return;
        }

        var token = _streamCancellationTokenSource?.Token ?? CancellationToken.None;
        lock (_udpLock)
        {
            if (isRtp)
            {
                _rtpDestination ??= remoteEndPoint;
            }
            else
            {
                _rtcpDestination ??= remoteEndPoint;
            }
        }

        TraceAudio($"Audio UDP probe received: {(isRtp ? "RTP" : "RTCP")} from {remoteEndPoint}");
        TryStartCaptureIfReady(token);
    }

    private void StartCommonLoops(CancellationToken cancellationToken)
    {
        if (!_statsStarted)
        {
            _statsStarted = true;
            _ = RunStatsLoopAsync(cancellationToken);
        }

        if (!_rtcpStarted)
        {
            _rtcpStarted = true;
            _ = RunRtcpLoopAsync(cancellationToken);
        }
    }

    private void TryStartCaptureIfReady(CancellationToken cancellationToken)
    {
        if (!_isSender || _captureStarted || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        IPEndPoint? rtpDestination;
        lock (_udpLock)
        {
            rtpDestination = _rtpDestination;
        }

        if (rtpDestination == null)
        {
            return;
        }

        _captureStarted = true;
        lock (_stateLock)
        {
            _state = StateStreaming;
        }

        _ = RunCaptureLoopAsync(Interlocked.Read(ref _streamId), cancellationToken);
        EmitState("Streaming Windows system audio");
    }

    private void StartPlaybackIfNeeded(CancellationToken cancellationToken)
    {
        if (_isSender || _playbackStarted || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _playbackStarted = true;
        lock (_stateLock)
        {
            _state = StateStreaming;
        }

        _ = RunPlaybackLoopAsync(cancellationToken);
        EmitState("Playing peer audio");
    }

    private async Task RunCaptureLoopAsync(long streamId, CancellationToken cancellationToken)
    {
        var captureChannel = Channel.CreateBounded<CapturePacket>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        try
        {
            var configuredBitrateBps = _configuredBitrateBps;
            using var encoder = new LibOpusAudioEncoder(configuredBitrateBps);
            TraceAudio($"Capture encoder started: quality={_qualityMode} configuredBitrateBps={configuredBitrateBps}");
            var pendingSamples = new List<short>(AudioProtocol.SamplesPerFrame * 4);
            using var capture = new WasapiLoopbackCapture();
            _loopbackCapture = capture;

            capture.DataAvailable += (_, args) =>
            {
                var copy = new byte[args.BytesRecorded];
                Buffer.BlockCopy(args.Buffer, 0, copy, 0, args.BytesRecorded);
                captureChannel.Writer.TryWrite(new CapturePacket(copy, args.BytesRecorded, capture.WaveFormat));
            };
            capture.RecordingStopped += (_, args) =>
            {
                captureChannel.Writer.TryComplete(args.Exception);
            };
            capture.StartRecording();

            await foreach (var packet in captureChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var converted = AudioSampleConverter.ToMono48Pcm16(packet.Buffer, packet.BytesRecorded, packet.WaveFormat);
                pendingSamples.AddRange(converted);
                while (pendingSamples.Count >= AudioProtocol.SamplesPerFrame)
                {
                    var pcmFrame = pendingSamples.Take(AudioProtocol.SamplesPerFrame).ToArray();
                    pendingSamples.RemoveRange(0, AudioProtocol.SamplesPerFrame);
                    byte[] payload;
                    try
                    {
                        payload = encoder.Encode(pcmFrame);
                    }
                    catch
                    {
                        Interlocked.Increment(ref _encodeErrors);
                        continue;
                    }

                    if (payload.Length == 0)
                    {
                        continue;
                    }

                    var sequence = _rtpSequence;
                    var timestamp = _rtpTimestamp;
                    _rtpSequence = RtpPacket.NextSequence(_rtpSequence);
                    _rtpTimestamp = unchecked(_rtpTimestamp + AudioProtocol.RtpTimestampIncrement);
                    var rtpPacket = new RtpPacket(
                        AudioProtocol.RtpPayloadType,
                        sequence,
                        timestamp,
                        _localSsrc,
                        payload).Encode();

                    UdpClient? client;
                    IPEndPoint? destination;
                    lock (_udpLock)
                    {
                        client = _rtpClient;
                        destination = _rtpDestination;
                    }

                    if (client == null || destination == null)
                    {
                        continue;
                    }

                    await SendUdpAsync(client, rtpPacket, destination, cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesSent, payload.Length);
                    Interlocked.Increment(ref _packetsSent);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await FailAudioAsync(AudioProtocol.ErrorCaptureFailed, $"System audio capture failed: {ex.Message}", streamId).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                _loopbackCapture?.StopRecording();
            }
            catch
            {
            }

            _loopbackCapture?.Dispose();
            _loopbackCapture = null;
            captureChannel.Writer.TryComplete();
        }
    }

    private async Task RunPlaybackLoopAsync(CancellationToken cancellationToken)
    {
        WasapiOut? output = null;
        try
        {
            using var decoder = new LibOpusAudioDecoder();
            var waveFormat = new WaveFormat(AudioProtocol.SampleRate, 16, AudioProtocol.Channels);
            var profile = JitterBuffer.ProfileFor(_latencyMode);
            var provider = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(profile.MaximumDelayMs + 80),
                DiscardOnBufferOverflow = true
            };

            output = new WasapiOut(
                AudioClientShareMode.Shared,
                useEventSync: false,
                latency: _latencyMode == AudioProtocol.LatencyModeStable ? 80 : 30);
            output.Init(provider);
            output.Play();

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_jitterBuffer.TryReadNext(out var read))
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                short[] pcm;
                if (read.IsMissing || read.Payload == null)
                {
                    pcm = decoder.DecodeMissing();
                }
                else
                {
                    try
                    {
                        pcm = decoder.Decode(read.Payload);
                    }
                    catch
                    {
                        Interlocked.Increment(ref _decodeErrors);
                        Interlocked.Increment(ref _playbackSilenceFillCount);
                        pcm = new short[AudioProtocol.SamplesPerFrame];
                    }
                }

                var bytes = AudioSampleConverter.Pcm16ToBytes(pcm, AudioProtocol.SamplesPerFrame);
                provider.AddSamples(bytes, 0, bytes.Length);
                await Task.Delay(AudioProtocol.FrameDurationMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await FailAudioAsync(AudioProtocol.ErrorPlaybackFailed, $"Audio playback failed: {ex.Message}", Interlocked.Read(ref _streamId)).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                output?.Stop();
            }
            catch
            {
            }

            output?.Dispose();
        }
    }

    private async Task RunRtcpLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                UdpClient? client;
                IPEndPoint? destination;
                lock (_udpLock)
                {
                    client = _rtcpClient;
                    destination = _rtcpDestination;
                }

                if (client == null || destination == null || _localSsrc == 0)
                {
                    continue;
                }

                var report = _isSender
                    ? RtcpProtocol.EncodeSenderReport(new RtcpSenderReport(
                        _localSsrc,
                        RtcpProtocol.NtpTimestampNow(),
                        _rtpTimestamp,
                        unchecked((uint)Interlocked.Read(ref _packetsSent)),
                        unchecked((uint)Interlocked.Read(ref _bytesSent))))
                    : RtcpProtocol.EncodeReceiverReport(BuildReceiverReport());

                await SendUdpAsync(client, report, destination, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _rtcpSendErrors);
                TraceAudio($"RTCP send failed: {ex.Message}");
            }
        }
    }

    private RtcpReceiverReport BuildReceiverReport()
    {
        var snapshot = _jitterBuffer.Snapshot();
        var packetsReceived = Interlocked.Read(ref _packetsReceived);
        var packetsLost = snapshot.SequenceGaps;
        var receivedDelta = packetsReceived - _lastRtcpReceivedPackets;
        var lostDelta = packetsLost - _lastRtcpLostPackets;
        _lastRtcpReceivedPackets = packetsReceived;
        _lastRtcpLostPackets = packetsLost;

        var periodPackets = Math.Max(0, receivedDelta + lostDelta);
        var fractionLost = periodPackets == 0
            ? (byte)0
            : (byte)Math.Clamp((int)Math.Round(lostDelta * 256.0 / periodPackets), 0, 255);

        uint jitter;
        lock (_rtpStatsLock)
        {
            jitter = unchecked((uint)Math.Max(0, Math.Round(_interarrivalJitter)));
        }

        var delay = _lastSenderReportArrivalCompact == 0 ? 0 : RtcpProtocol.DelaySince(_lastSenderReportArrivalCompact);
        return new RtcpReceiverReport(
            _localSsrc,
            _peerSsrc,
            fractionLost,
            unchecked((int)Math.Clamp(packetsLost, -0x800000, 0x7fffff)),
            unchecked((uint)Math.Max(0, Interlocked.Read(ref _highestSequenceReceived))),
            jitter,
            _lastSenderReportCompact,
            delay);
    }

    private void HandleRtcpPacket(RtcpPacket packet)
    {
        switch (packet)
        {
            case RtcpSenderReport senderReport when senderReport.Ssrc == _peerSsrc:
                _lastSenderReportCompact = RtcpProtocol.CompactNtp(senderReport.NtpTimestamp);
                _lastSenderReportArrivalCompact = RtcpProtocol.CompactNtpNow();
                break;
            case RtcpReceiverReport receiverReport when receiverReport.ReportedSsrc == _localSsrc:
                _rtcpFractionLost = receiverReport.FractionLost;
                _rtcpReportedJitter = receiverReport.InterarrivalJitter;
                if (receiverReport.LastSenderReport != 0)
                {
                    var rttCompact = unchecked(RtcpProtocol.CompactNtpNow() - receiverReport.LastSenderReport - receiverReport.DelaySinceLastSenderReport);
                    _rtcpRttMs = Math.Max(0, RtcpProtocol.CompactNtpToMilliseconds(rttCompact));
                }
                break;
        }
    }

    private async Task RunStatsLoopAsync(CancellationToken cancellationToken)
    {
        var lastBytes = 0L;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var totalBytes = _isSender ? Interlocked.Read(ref _bytesSent) : Interlocked.Read(ref _bytesReceived);
                var bitrateBps = Math.Max(0, totalBytes - lastBytes) * 8;
                lastBytes = totalBytes;
                var snapshot = _jitterBuffer.Snapshot();
                var packetsSent = Interlocked.Read(ref _packetsSent);
                var packetsReceived = Interlocked.Read(ref _packetsReceived);
                long rttMs;
                byte fractionLost;
                uint reportedJitter;
                uint localJitter;
                lock (_rtpStatsLock)
                {
                    rttMs = _rtcpRttMs;
                    fractionLost = _rtcpFractionLost;
                    reportedJitter = _rtcpReportedJitter;
                    localJitter = unchecked((uint)Math.Max(0, Math.Round(_interarrivalJitter)));
                }

                EmitStats(new AudioStatsEventArgs(
                    _mode,
                    _state,
                    Interlocked.Read(ref _streamId),
                    bitrateBps,
                    snapshot.BufferLevelMs,
                    packetsSent,
                    packetsReceived,
                    snapshot.DroppedFrames,
                    snapshot.UnderflowCount,
                    rttMs,
                    _latencyMode,
                    _qualityMode,
                    _configuredBitrateBps,
                    snapshot.SequenceGaps,
                    snapshot.LatePacketDrops,
                    snapshot.DuplicateOrReorderedPackets,
                    snapshot.PlcCount + Interlocked.Read(ref _playbackSilenceFillCount),
                    fractionLost,
                    reportedJitter,
                    localJitter,
                    Interlocked.Read(ref _udpSendErrors),
                    Interlocked.Read(ref _udpReceiveErrors),
                    Interlocked.Read(ref _encodeErrors),
                    Interlocked.Read(ref _decodeErrors)));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendUdpAsync(
        UdpClient client,
        byte[] payload,
        IPEndPoint destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.SendAsync(payload, payload.Length, destination)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _udpSendErrors);
            if (_isSender)
            {
                await FailAudioAsync(AudioProtocol.ErrorRtpSendFailed, $"Audio UDP send failed: {ex.Message}", Interlocked.Read(ref _streamId)).ConfigureAwait(false);
            }
        }
    }

    private void TrackRtpArrival(RtpPacket packet)
    {
        lock (_rtpStatsLock)
        {
            var arrivalRtpUnits = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * AudioProtocol.RtpClockRate / 1000;
            var transit = arrivalRtpUnits - packet.Timestamp;
            if (_lastTransit.HasValue)
            {
                var delta = Math.Abs(transit - _lastTransit.Value);
                _interarrivalJitter += (delta - _interarrivalJitter) / 16.0;
            }

            _lastTransit = transit;
        }

        var extendedSequence = ExtendReceivedSequence(packet.SequenceNumber);
        Interlocked.Exchange(ref _highestSequenceReceived, Math.Max(Interlocked.Read(ref _highestSequenceReceived), extendedSequence));
    }

    private long ExtendReceivedSequence(ushort sequence)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _highestSequenceReceived);
            if (current < 0)
            {
                if (Interlocked.CompareExchange(ref _highestSequenceReceived, sequence, current) == current)
                {
                    return sequence;
                }

                continue;
            }

            var cycle = current & ~0xffffL;
            var candidate = cycle + sequence;
            if (candidate - current > 32767)
            {
                candidate -= 65536;
            }
            else if (current - candidate > 32768)
            {
                candidate += 65536;
            }

            return candidate;
        }
    }

    private async Task SendAudioControlAsync(string metadataJson, CancellationToken cancellationToken = default)
    {
        var sessionManager = _sessionManager ?? throw new InvalidOperationException("WDCable session manager is not available.");
        var sessionId = sessionManager.CurrentSessionId ?? "";
        var node = JsonNode.Parse(metadataJson)?.AsObject() ?? new JsonObject();
        node["sessionId"] = sessionId;
        node["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var streamId = node.TryGetPropertyValue("streamId", out var streamNode) && streamNode != null
            ? streamNode.GetValue<long>()
            : 0;
        await sessionManager.SendAudioControlAsync(node.ToJsonString(), streamId, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAudioErrorAsync(long streamId, string code, string message)
    {
        if (_sessionManager?.IsReady != true)
        {
            return;
        }

        try
        {
            await _sessionManager.SendAudioFeatureErrorAsync(streamId, code, message).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task FailAudioAsync(string code, string message, long streamId)
    {
        await SendAudioErrorAsync(streamId, code, message).ConfigureAwait(false);
        EmitAudioError(code, message, streamId);
        CleanupLocal("failed", emitStopped: true);
    }

    private async Task EmitLocalStartErrorAsync(string code, string message)
    {
        await SendAudioErrorAsync(0, code, message).ConfigureAwait(false);
        EmitAudioError(code, message, 0);
    }

    private void HandleErrorFrame(string metadataJson, long fallbackStreamId)
    {
        Dictionary<string, JsonElement> metadata;
        try
        {
            metadata = AudioProtocol.ParseMetadata(metadataJson);
        }
        catch
        {
            return;
        }

        var code = AudioProtocol.OptionalString(metadata, "code");
        if (!code.StartsWith("audio_", StringComparison.Ordinal))
        {
            return;
        }

        var message = AudioProtocol.OptionalString(metadata, "message", "Peer reported audio error.");
        var streamId = AudioProtocol.OptionalInt64(metadata, "streamId", fallbackStreamId);
        EmitAudioError(code, message, streamId);
        CleanupLocal("peer_error", emitStopped: true);
    }

    private AudioSessionInfo RequireAudioSession()
    {
        var info = _sessionManager?.GetAudioSessionInfo();
        return info ?? throw new InvalidOperationException("The WDCable session is not ready.");
    }

    private CancellationToken ReplaceStreamCancellation()
    {
        _streamCancellationTokenSource?.Cancel();
        _streamCancellationTokenSource?.Dispose();
        _streamCancellationTokenSource = new CancellationTokenSource();
        return _streamCancellationTokenSource.Token;
    }

    private void CleanupLocal(string reason, bool emitStopped)
    {
        var previousState = _state;
        _streamCancellationTokenSource?.Cancel();
        _streamCancellationTokenSource?.Dispose();
        _streamCancellationTokenSource = null;

        try
        {
            _loopbackCapture?.StopRecording();
        }
        catch
        {
        }

        CloseUdpSockets();
        _jitterBuffer.Clear();
        lock (_stateLock)
        {
            ResetStateLocked(ModeIdle, StateIdle, streamId: 0, offerId: "", isSender: false);
            _latencyMode = LoadLatencyMode();
            _qualityMode = LoadQualityMode();
            _configuredBitrateBps = AudioProtocol.BitrateForQualityMode(_qualityMode);
            _peerReady = false;
            _captureStarted = false;
            _playbackStarted = false;
            _statsStarted = false;
            _rtcpStarted = false;
            _peerSsrc = 0;
            _localSsrc = 0;
        }

        if (emitStopped && previousState != StateIdle)
        {
            EmitState(reason);
        }
    }

    private void CloseUdpSockets()
    {
        UdpClient? rtpClient;
        UdpClient? rtcpClient;
        lock (_udpLock)
        {
            rtpClient = _rtpClient;
            rtcpClient = _rtcpClient;
            _rtpClient = null;
            _rtcpClient = null;
            _rtpDestination = null;
            _rtcpDestination = null;
        }

        try
        {
            rtpClient?.Dispose();
        }
        catch
        {
        }

        try
        {
            rtcpClient?.Dispose();
        }
        catch
        {
        }
    }

    private void ResetStateLocked(string mode, string state, long streamId, string offerId, bool isSender)
    {
        _mode = mode;
        _state = state;
        _streamId = streamId;
        _offerId = offerId;
        _isSender = isSender;
        _captureStarted = false;
        _playbackStarted = false;
        _statsStarted = false;
        _rtcpStarted = false;
    }

    private void ResetStats()
    {
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _packetsSent, 0);
        Interlocked.Exchange(ref _packetsReceived, 0);
        Interlocked.Exchange(ref _rtcpSendErrors, 0);
        Interlocked.Exchange(ref _udpSendErrors, 0);
        Interlocked.Exchange(ref _udpReceiveErrors, 0);
        Interlocked.Exchange(ref _encodeErrors, 0);
        Interlocked.Exchange(ref _decodeErrors, 0);
        Interlocked.Exchange(ref _playbackSilenceFillCount, 0);
        Interlocked.Exchange(ref _highestSequenceReceived, -1);
        _lastRtcpReceivedPackets = 0;
        _lastRtcpLostPackets = 0;
        _lastSenderReportCompact = 0;
        _lastSenderReportArrivalCompact = 0;
        _rtcpFractionLost = 0;
        _rtcpReportedJitter = 0;
        _rtcpRttMs = -1;
        lock (_rtpStatsLock)
        {
            _interarrivalJitter = 0;
            _lastTransit = null;
        }

        _jitterBuffer = new JitterBuffer(_latencyMode);
    }

    private static IPAddress ParseLocalAddress(string? address)
    {
        return IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Any;
    }

    private static IPAddress ParseRemoteAddress(string? address)
    {
        return IPAddress.TryParse(address, out var parsed)
            ? parsed
            : throw new InvalidOperationException("Peer audio address is unavailable.");
    }

    private static bool CanCreateLoopbackCapture(out string error)
    {
        try
        {
            using var capture = new WasapiLoopbackCapture();
            _ = capture.WaveFormat;
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"System audio capture is unavailable: {ex.Message}";
            return false;
        }
    }

    private void EmitState(string message)
    {
        var source = _mode == ModeSend ? AudioProtocol.SourceSystemAudio : AudioProtocol.SourceMicrophone;
        var args = new AudioStateChangedEventArgs(
            _mode,
            _state,
            Interlocked.Read(ref _streamId),
            source,
            AudioProtocol.CodecOpus,
            _peerReady,
            _state == StateStreaming,
            message,
            _latencyMode,
            _qualityMode);
        RaiseOnDispatcher(() => StateChanged?.Invoke(this, args));
        RaiseOnDispatcher(() => StatusChanged?.Invoke(this, message));
    }

    private void EmitStats(AudioStatsEventArgs args)
    {
        RaiseOnDispatcher(() => StatsChanged?.Invoke(this, args));
    }

    private void EmitAudioError(string code, string message, long streamId)
    {
        RaiseOnDispatcher(() => ErrorOccurred?.Invoke(this, new AudioErrorEventArgs(code, message, streamId)));
    }

    private void RaiseOnDispatcher(Action action)
    {
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
        else
        {
            action();
        }
    }

    private static string LoadLatencyMode()
    {
        try
        {
            var dataManager = ServiceManager.DataManager;
            return dataManager == null
                ? AudioProtocol.LatencyModeLow
                : AudioProtocol.NormalizeLatencyMode(
                    dataManager.GetSetting("AudioLatencyMode", AudioProtocol.LatencyModeLow));
        }
        catch
        {
            return AudioProtocol.LatencyModeLow;
        }
    }

    private static void SaveLatencyMode(string latencyMode)
    {
        try
        {
            ServiceManager.DataManager?.SetSetting("AudioLatencyMode", latencyMode);
        }
        catch
        {
        }
    }

    private static string LoadQualityMode()
    {
        try
        {
            var dataManager = ServiceManager.DataManager;
            return dataManager == null
                ? AudioProtocol.QualityStandard
                : AudioProtocol.NormalizeQualityMode(
                    dataManager.GetSetting("AudioQualityMode", AudioProtocol.QualityStandard));
        }
        catch
        {
            return AudioProtocol.QualityStandard;
        }
    }

    private static void SaveQualityMode(string qualityMode)
    {
        try
        {
            ServiceManager.DataManager?.SetSetting("AudioQualityMode", qualityMode);
        }
        catch
        {
        }
    }

    private static void TraceAudio(string message)
    {
        Debug.WriteLine($"Audio Link v2: {message}");
    }

    private sealed record CapturePacket(byte[] Buffer, int BytesRecorded, WaveFormat WaveFormat);
}

public sealed class AudioStateChangedEventArgs : EventArgs
{
    public AudioStateChangedEventArgs(
        string mode,
        string state,
        long streamId,
        string source,
        string encoding,
        bool peerReady,
        bool isStreaming,
        string message,
        string latencyMode,
        string qualityMode)
    {
        Mode = mode;
        State = state;
        StreamId = streamId;
        Source = source;
        Encoding = encoding;
        PeerReady = peerReady;
        IsStreaming = isStreaming;
        Message = message;
        LatencyMode = latencyMode;
        QualityMode = qualityMode;
    }

    public string Mode { get; }

    public string State { get; }

    public long StreamId { get; }

    public string Source { get; }

    public string Encoding { get; }

    public bool PeerReady { get; }

    public bool IsStreaming { get; }

    public string Message { get; }

    public string LatencyMode { get; }

    public string QualityMode { get; }
}

public sealed class AudioStatsEventArgs : EventArgs
{
    public AudioStatsEventArgs(
        string mode,
        string state,
        long streamId,
        long bitrateBps,
        int bufferLevelMs,
        long framesSent,
        long framesReceived,
        long droppedFrames,
        long underflowCount,
        long latencyMs,
        string latencyMode,
        string qualityMode,
        int configuredBitrateBps,
        long packetLossCount,
        long latePacketDrops,
        long duplicateOrReorderedPackets,
        long plcCount,
        byte rtcpFractionLost,
        uint rtcpJitter,
        uint localJitter,
        long udpSendErrors,
        long udpReceiveErrors,
        long encodeErrors,
        long decodeErrors)
    {
        Mode = mode;
        State = state;
        StreamId = streamId;
        BitrateBps = bitrateBps;
        BufferLevelMs = bufferLevelMs;
        FramesSent = framesSent;
        FramesReceived = framesReceived;
        DroppedFrames = droppedFrames;
        UnderflowCount = underflowCount;
        LatencyMs = latencyMs;
        LatencyMode = latencyMode;
        QualityMode = qualityMode;
        ConfiguredBitrateBps = configuredBitrateBps;
        PacketLossCount = packetLossCount;
        LatePacketDrops = latePacketDrops;
        DuplicateOrReorderedPackets = duplicateOrReorderedPackets;
        PlcCount = plcCount;
        RtcpFractionLost = rtcpFractionLost;
        RtcpJitter = rtcpJitter;
        LocalJitter = localJitter;
        UdpSendErrors = udpSendErrors;
        UdpReceiveErrors = udpReceiveErrors;
        EncodeErrors = encodeErrors;
        DecodeErrors = decodeErrors;
    }

    public string Mode { get; }

    public string State { get; }

    public long StreamId { get; }

    public long BitrateBps { get; }

    public int BufferLevelMs { get; }

    public long FramesSent { get; }

    public long FramesReceived { get; }

    public long DroppedFrames { get; }

    public long UnderflowCount { get; }

    public long LatencyMs { get; }

    public string LatencyMode { get; }

    public string QualityMode { get; }

    public int ConfiguredBitrateBps { get; }

    public long PacketLossCount { get; }

    public long LatePacketDrops { get; }

    public long DuplicateOrReorderedPackets { get; }

    public long PlcCount { get; }

    public byte RtcpFractionLost { get; }

    public uint RtcpJitter { get; }

    public uint LocalJitter { get; }

    public long UdpSendErrors { get; }

    public long UdpReceiveErrors { get; }

    public long EncodeErrors { get; }

    public long DecodeErrors { get; }
}

public sealed class AudioErrorEventArgs : EventArgs
{
    public AudioErrorEventArgs(string code, string message, long streamId)
    {
        Code = code;
        Message = message;
        StreamId = streamId;
    }

    public string Code { get; }

    public string Message { get; }

    public long StreamId { get; }
}
