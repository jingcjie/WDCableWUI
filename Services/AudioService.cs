using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Concentus;
using Concentus.Enums;
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

    private static AudioService? _instance;
    private static readonly object InstanceLock = new();

    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly object _stateLock = new();
    private readonly JitterBuffer _jitterBuffer = new();
    private SessionManager? _sessionManager;
    private CancellationTokenSource? _streamCancellationTokenSource;
    private WasapiLoopbackCapture? _loopbackCapture;
    private long _audioSequence;
    private long _bytesSent;
    private long _bytesReceived;
    private long _framesSent;
    private long _framesReceived;
    private long _extraDroppedFrames;
    private string _mode = ModeIdle;
    private string _state = StateIdle;
    private long _streamId;
    private string _offerId = "";
    private bool _isSender;
    private bool _peerReady;
    private bool _accepted;
    private bool _listenerStarted;
    private bool _isDisposed;

    private AudioService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
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

    public async Task StartReceiveAsync(CancellationToken cancellationToken = default)
    {
        var sessionInfo = RequireAudioSession();
        if (!AudioProtocol.PeerSupportsAudio(sessionInfo.PeerCapabilities))
        {
            await EmitLocalStartErrorAsync(AudioProtocol.ErrorUnsupported, "The connected peer does not advertise Audio Link.").ConfigureAwait(false);
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

            ResetStateLocked(ModeReceive, StateReceiveReady, streamId: 0, offerId: "", isSender: false);
        }

        ResetStats();
        await SendAudioControlAsync(AudioProtocol.ReceiveReady(0), cancellationToken).ConfigureAwait(false);
        EmitState("Ready to receive audio");
    }

    public async Task StartSendAsync(CancellationToken cancellationToken = default)
    {
        var sessionInfo = RequireAudioSession();
        if (!AudioProtocol.PeerSupportsAudio(sessionInfo.PeerCapabilities))
        {
            await EmitLocalStartErrorAsync(AudioProtocol.ErrorUnsupported, "The connected peer does not advertise Audio Link.").ConfigureAwait(false);
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

            ResetStateLocked(ModeSend, StateOfferSent, streamId, offerId, isSender: true);
        }

        ResetStats();
        await SendAudioControlAsync(AudioProtocol.Offer(streamId, offerId), cancellationToken).ConfigureAwait(false);
        EmitState("System audio offer sent");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var currentStreamId = Interlocked.Read(ref _streamId);
        var currentMode = _mode;
        if (currentStreamId != 0 && _state != StateIdle)
        {
            await SendAudioControlAsync(AudioProtocol.Stop(currentStreamId, "local_stop"), cancellationToken).ConfigureAwait(false);
        }

        if (currentMode == ModeReceive)
        {
            await SendAudioControlAsync(AudioProtocol.ReceiveStopped(currentStreamId), cancellationToken).ConfigureAwait(false);
        }

        CleanupLocal("stopped", emitStopped: true);
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
        sessionManager.AudioFrameReceived += OnAudioFrameReceived;
        sessionManager.AudioTransportReady += OnAudioTransportReady;
        sessionManager.AudioTransportClosed += OnAudioTransportClosed;
        sessionManager.StateChanged += OnSessionStateChanged;
        sessionManager.SessionFailed += OnSessionFailed;
    }

    private void UnsubscribeFromSession(SessionManager sessionManager)
    {
        sessionManager.ControlFrameReceived -= OnControlFrameReceived;
        sessionManager.AudioFrameReceived -= OnAudioFrameReceived;
        sessionManager.AudioTransportReady -= OnAudioTransportReady;
        sessionManager.AudioTransportClosed -= OnAudioTransportClosed;
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
            case AudioProtocol.KindTransport:
                _ = HandleTransportAsync(metadata);
                break;
            case AudioProtocol.KindStop:
                CleanupLocal(AudioProtocol.OptionalString(metadata, "reason", "peer_stop"), emitStopped: true);
                break;
        }
    }

    private void OnAudioFrameReceived(object? sender, ProtocolFrameReceivedEventArgs e)
    {
        try
        {
            if (_isSender || e.Frame.StreamId != Interlocked.Read(ref _streamId) || _state == StateIdle)
            {
                Interlocked.Increment(ref _extraDroppedFrames);
                return;
            }

            var frame = AudioProtocol.ParseAudioFrame(e.Frame);
            _jitterBuffer.Add(frame);
            if (!frame.CodecConfig)
            {
                Interlocked.Add(ref _bytesReceived, frame.Payload.Length);
                Interlocked.Increment(ref _framesReceived);
            }
        }
        catch
        {
            Interlocked.Increment(ref _extraDroppedFrames);
        }
    }

    private void OnAudioTransportReady(object? sender, AudioTransportEventArgs e)
    {
        if (e.StreamId != Interlocked.Read(ref _streamId) || _state is StateIdle or StateStreaming)
        {
            return;
        }

        lock (_stateLock)
        {
            _state = StateStreaming;
        }

        var streamToken = ReplaceStreamCancellation();
        _ = RunStatsLoopAsync(streamToken);
        if (_isSender)
        {
            _ = RunCaptureLoopAsync(e.StreamId, streamToken);
        }
        else
        {
            _ = RunPlaybackLoopAsync(streamToken);
        }

        EmitState(_isSender ? "Streaming Windows system audio" : "Playing peer audio");
    }

    private void OnAudioTransportClosed(object? sender, AudioTransportClosedEventArgs e)
    {
        if (_state != StateIdle && e.StreamId == Interlocked.Read(ref _streamId))
        {
            EmitAudioError(AudioProtocol.ErrorTransportFailed, "Audio transport closed", e.StreamId);
            CleanupLocal(e.Reason, emitStopped: true);
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
            await SendAudioErrorAsync(0, AudioProtocol.ErrorUnsupported, ex.Message).ConfigureAwait(false);
            return;
        }

        if (_state != StateReceiveReady || _mode != ModeReceive)
        {
            await SendAudioErrorAsync(offer.StreamId, AudioProtocol.ErrorReceiverNotReady, "Receiver has not started Audio Link receive mode.").ConfigureAwait(false);
            return;
        }

        if (offer.Codec != AudioProtocol.CodecOpus ||
            offer.SampleRate != AudioProtocol.SampleRate ||
            offer.Channels != AudioProtocol.Channels ||
            offer.FrameDurationMs != AudioProtocol.FrameDurationMs)
        {
            await SendAudioErrorAsync(offer.StreamId, AudioProtocol.ErrorUnsupported, "Unsupported audio offer.").ConfigureAwait(false);
            return;
        }

        lock (_stateLock)
        {
            _streamId = offer.StreamId;
            _offerId = offer.OfferId;
            _accepted = true;
            _isSender = false;
            _state = StateConnecting;
        }

        await SendAudioControlAsync(AudioProtocol.Accept(offer.StreamId, offer.OfferId)).ConfigureAwait(false);
        EmitState("Audio offer accepted");
        await OpenListenerIfGroupOwnerAsync().ConfigureAwait(false);
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
            await FailAudioAsync(AudioProtocol.ErrorUnsupported, ex.Message, Interlocked.Read(ref _streamId)).ConfigureAwait(false);
            return;
        }

        if (!_isSender || accept.StreamId != Interlocked.Read(ref _streamId) || accept.OfferId != _offerId)
        {
            return;
        }

        if (accept.Codec != AudioProtocol.CodecOpus)
        {
            await FailAudioAsync(AudioProtocol.ErrorUnsupported, "Peer accepted an unsupported codec.", accept.StreamId).ConfigureAwait(false);
            return;
        }

        lock (_stateLock)
        {
            _accepted = true;
            _state = StateConnecting;
        }

        EmitState("Audio offer accepted by peer");
        await OpenListenerIfGroupOwnerAsync().ConfigureAwait(false);
    }

    private async Task HandleTransportAsync(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        AudioTransportOffer transport;
        try
        {
            transport = AudioProtocol.ParseTransport(metadata);
        }
        catch (FormatException ex)
        {
            await FailAudioAsync(AudioProtocol.ErrorTransportFailed, ex.Message, Interlocked.Read(ref _streamId)).ConfigureAwait(false);
            return;
        }

        var sessionInfo = _sessionManager?.GetAudioSessionInfo();
        if (transport.StreamId != Interlocked.Read(ref _streamId) ||
            transport.Transport != AudioProtocol.TransportTcp ||
            transport.Port <= 0)
        {
            await FailAudioAsync(AudioProtocol.ErrorTransportFailed, "Invalid audio transport details.", Interlocked.Read(ref _streamId)).ConfigureAwait(false);
            return;
        }

        if (sessionInfo?.Role != SessionRole.Client)
        {
            return;
        }

        try
        {
            EmitState("Connecting audio channel");
            await _sessionManager!.ConnectAudioTransportAsync(transport.StreamId, transport.Port).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FailAudioAsync(AudioProtocol.ErrorTransportFailed, $"Failed to connect audio channel: {ex.Message}", transport.StreamId).ConfigureAwait(false);
        }
    }

    private async Task OpenListenerIfGroupOwnerAsync()
    {
        var sessionInfo = _sessionManager?.GetAudioSessionInfo();
        if (sessionInfo?.Role != SessionRole.GroupOwner || _listenerStarted || !_accepted)
        {
            return;
        }

        _listenerStarted = true;
        var streamId = Interlocked.Read(ref _streamId);
        try
        {
            var port = await _sessionManager!.StartAudioListenerAsync(streamId).ConfigureAwait(false);
            await SendAudioControlAsync(AudioProtocol.Transport(streamId, port)).ConfigureAwait(false);
            EmitState($"Audio channel listening on port {port}");
        }
        catch (Exception ex)
        {
            await FailAudioAsync(AudioProtocol.ErrorTransportFailed, $"Audio listener failed: {ex.Message}", streamId).ConfigureAwait(false);
        }
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
            var encoder = OpusCodecFactory.CreateEncoder(
                AudioProtocol.SampleRate,
                AudioProtocol.Channels,
                OpusApplication.OPUS_APPLICATION_AUDIO,
                null);
            encoder.Bitrate = AudioProtocol.BitrateBps;
            var pendingSamples = new List<short>(AudioProtocol.SamplesPerFrame * 4);
            using var capture = new WasapiLoopbackCapture();
            _loopbackCapture = capture;
            await SendOpusCodecConfigFramesAsync(streamId, cancellationToken).ConfigureAwait(false);

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
                    var output = new byte[4096];
                    var encodedBytes = encoder.Encode(pcmFrame, AudioProtocol.SamplesPerFrame, output, output.Length);
                    if (encodedBytes <= 0)
                    {
                        continue;
                    }

                    var payload = output[..encodedBytes];
                    await _sessionManager!.WriteAudioFrameAsync(
                        streamId,
                        Interlocked.Increment(ref _audioSequence),
                        AudioProtocol.FrameMetadata(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        payload,
                        cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesSent, payload.Length);
                    Interlocked.Increment(ref _framesSent);
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

    private async Task SendOpusCodecConfigFramesAsync(long streamId, CancellationToken cancellationToken)
    {
        var packets = AudioProtocol.AndroidOpusCodecConfigPackets();
        for (var index = 0; index < packets.Count; index++)
        {
            await _sessionManager!.WriteAudioFrameAsync(
                streamId,
                Interlocked.Increment(ref _audioSequence),
                AudioProtocol.FrameMetadata(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    codecConfig: true,
                    codecConfigIndex: index),
                packets[index],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunPlaybackLoopAsync(CancellationToken cancellationToken)
    {
        WasapiOut? output = null;
        try
        {
            var decoder = OpusCodecFactory.CreateDecoder(AudioProtocol.SampleRate, AudioProtocol.Channels, null);
            var waveFormat = new WaveFormat(AudioProtocol.SampleRate, 16, AudioProtocol.Channels);
            var provider = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true
            };

            output = new WasapiOut(AudioClientShareMode.Shared, false, 50);
            output.Init(provider);
            output.Play();

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = _jitterBuffer.PollReady();
                if (frame == null)
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (frame.CodecConfig)
                {
                    continue;
                }

                var pcm = new short[AudioProtocol.SamplesPerFrame];
                var decodedSamples = decoder.Decode(frame.Payload, pcm, AudioProtocol.SamplesPerFrame, false);
                if (decodedSamples <= 0)
                {
                    continue;
                }

                var bytes = AudioSampleConverter.Pcm16ToBytes(pcm, decodedSamples);
                provider.AddSamples(bytes, 0, bytes.Length);
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
                EmitStats(new AudioStatsEventArgs(
                    _mode,
                    _state,
                    Interlocked.Read(ref _streamId),
                    bitrateBps,
                    snapshot.BufferLevelMs,
                    Interlocked.Read(ref _framesSent),
                    Interlocked.Read(ref _framesReceived),
                    snapshot.DroppedFrames + Interlocked.Read(ref _extraDroppedFrames),
                    snapshot.UnderflowCount,
                    -1));
            }
        }
        catch (OperationCanceledException)
        {
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

        _jitterBuffer.Clear();
        _sessionManager?.CloseAudioTransport();
        lock (_stateLock)
        {
            ResetStateLocked(ModeIdle, StateIdle, streamId: 0, offerId: "", isSender: false);
            _peerReady = false;
            _accepted = false;
            _listenerStarted = false;
        }

        if (emitStopped && previousState != StateIdle)
        {
            EmitState(reason);
        }
    }

    private void ResetStateLocked(string mode, string state, long streamId, string offerId, bool isSender)
    {
        _mode = mode;
        _state = state;
        _streamId = streamId;
        _offerId = offerId;
        _isSender = isSender;
        _accepted = false;
        _listenerStarted = false;
    }

    private void ResetStats()
    {
        Interlocked.Exchange(ref _audioSequence, 0);
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _framesSent, 0);
        Interlocked.Exchange(ref _framesReceived, 0);
        Interlocked.Exchange(ref _extraDroppedFrames, 0);
        _jitterBuffer.Clear();
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
            message);
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
        string message)
    {
        Mode = mode;
        State = state;
        StreamId = streamId;
        Source = source;
        Encoding = encoding;
        PeerReady = peerReady;
        IsStreaming = isStreaming;
        Message = message;
    }

    public string Mode { get; }

    public string State { get; }

    public long StreamId { get; }

    public string Source { get; }

    public string Encoding { get; }

    public bool PeerReady { get; }

    public bool IsStreaming { get; }

    public string Message { get; }
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
        long latencyMs)
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
