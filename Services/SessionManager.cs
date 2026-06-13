using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WDCableWUI.Protocol;
using Windows.ApplicationModel;

namespace WDCableWUI.Services;

public sealed class SessionManager : IDisposable
{
    private const int MaxConnectAttempts = 10;
    private const int BulkChunkSize = 64 * 1024;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

    private static SessionManager? _instance;
    private static readonly object InstanceLock = new();

    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SessionStateMachine _stateMachine = new();
    private readonly object _stateLock = new();

    private WiFiDirectService? _wifiDirectService;
    private ISessionTransportAdapter? _transportAdapter;
    private SessionRuntime? _runtime;
    private CancellationTokenSource? _sessionCancellationTokenSource;
    private SessionLinkInfo? _activeLink;
    private int _generation;
    private string? _lastDisconnectReason;
    private bool _isDisposed;

    private SessionManager()
        : this(new TcpSessionTransportAdapter())
    {
    }

    internal SessionManager(ISessionTransportAdapter transportAdapter)
    {
        _transportAdapter = transportAdapter;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public static SessionManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (InstanceLock)
                {
                    _instance ??= new SessionManager();
                }
            }

            return _instance;
        }
    }

    public static void ResetInstance()
    {
        lock (InstanceLock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionReadyEventArgs>? SessionReady;
    public event EventHandler<SessionFailedEventArgs>? SessionFailed;
    public event EventHandler<SessionFailedEventArgs>? PeerProtocolMissing;
    public event EventHandler<SessionDisconnectEventArgs>? DisconnectReasonChanged;
    public event EventHandler<ProtocolFrameReceivedEventArgs>? ControlFrameReceived;
    public event EventHandler<ProtocolFrameReceivedEventArgs>? BulkFrameReceived;

    public SessionPhase CurrentPhase
    {
        get
        {
            lock (_stateLock)
            {
                return _stateMachine.Phase;
            }
        }
    }

    public bool IsReady => CurrentPhase == SessionPhase.Ready && _runtime != null;

    public string? CurrentSessionId => _runtime?.SessionId ?? _activeLink?.SessionId;

    public SessionRole? CurrentRole => _runtime?.Role ?? _activeLink?.Role;

    public string? PeerName => _runtime?.PeerName ?? _activeLink?.PeerName;

    public string? PeerAddress => _runtime?.PeerAddress ?? _activeLink?.RemoteAddress;

    public string? LastDisconnectReason => _lastDisconnectReason;

    public void Initialize(WiFiDirectService wifiDirectService)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(wifiDirectService);

        if (_wifiDirectService != null)
        {
            _wifiDirectService.DeviceConnected -= OnWiFiDirectConnected;
            _wifiDirectService.DeviceDisconnected -= OnWiFiDirectDisconnected;
        }

        _wifiDirectService = wifiDirectService;
        _wifiDirectService.DeviceConnected += OnWiFiDirectConnected;
        _wifiDirectService.DeviceDisconnected += OnWiFiDirectDisconnected;
    }

    public Task StartFromCurrentWiFiDirectLinkAsync()
    {
        if (_wifiDirectService?.ConnectedDevice == null)
        {
            throw new InvalidOperationException("WiFi Direct must be connected before starting a WDCable session.");
        }

        return StartSessionAsync(_wifiDirectService.ConnectedDevice);
    }

    public async Task SendControlFrameAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var runtime = RequireReadyRuntime();
        var outbound = new ProtocolFrame(
            frame.Type,
            ProtocolChannel.Control,
            frame.Flags,
            frame.StreamId,
            frame.SequenceNumber == 0 ? runtime.NextSequenceNumber() : frame.SequenceNumber,
            frame.CorrelationId == default ? Guid.NewGuid() : frame.CorrelationId,
            frame.MetadataJson,
            frame.Payload);

        await runtime.ControlTransport.WriteFrameAsync(outbound, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendControlFrameAsync(
        ProtocolFrameType type,
        string metadataJson,
        byte[]? payload = null,
        long streamId = 0,
        Guid correlationId = default,
        CancellationToken cancellationToken = default)
    {
        var runtime = RequireReadyRuntime();
        var frame = new ProtocolFrame(
            type,
            ProtocolChannel.Control,
            streamId: streamId,
            sequenceNumber: runtime.NextSequenceNumber(),
            correlationId: correlationId == default ? Guid.NewGuid() : correlationId,
            metadataJson: metadataJson,
            payload: payload);

        await runtime.ControlTransport.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendBulkFrameAsync(
        ProtocolFrameType type,
        string metadataJson,
        byte[]? payload = null,
        long streamId = 0,
        Guid correlationId = default,
        CancellationToken cancellationToken = default)
    {
        var runtime = RequireReadyRuntime();
        var frame = new ProtocolFrame(
            type,
            ProtocolChannel.Bulk,
            streamId: streamId,
            sequenceNumber: runtime.NextSequenceNumber(),
            correlationId: correlationId == default ? Guid.NewGuid() : correlationId,
            metadataJson: metadataJson,
            payload: payload);

        await runtime.BulkTransport.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(string reason = "local_disconnect")
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            StopCurrentSessionLocked(reason, emitDisconnecting: true);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_wifiDirectService != null)
        {
            _wifiDirectService.DeviceConnected -= OnWiFiDirectConnected;
            _wifiDirectService.DeviceDisconnected -= OnWiFiDirectDisconnected;
            _wifiDirectService = null;
        }

        _sessionCancellationTokenSource?.Cancel();
        _sessionCancellationTokenSource?.Dispose();
        _sessionCancellationTokenSource = null;
        _runtime?.Close();
        _runtime = null;
        _transportAdapter?.Dispose();
        _transportAdapter = null;
        _lifecycleLock.Dispose();
    }

    private async void OnWiFiDirectConnected(object? sender, WiFiDirectDevice device)
    {
        try
        {
            await StartSessionAsync(device).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitError($"Failed to start session: {ex.Message}");
        }
    }

    private async void OnWiFiDirectDisconnected(object? sender, EventArgs e)
    {
        await DisconnectAsync("wifi_direct_disconnected").ConfigureAwait(false);
    }

    private async Task StartSessionAsync(WiFiDirectDevice device)
    {
        if (_wifiDirectService == null)
        {
            throw new InvalidOperationException("SessionManager has not been initialized with WiFiDirectService.");
        }

        var link = BuildLinkInfo(_wifiDirectService, device);
        CancellationTokenSource sessionCancellationTokenSource;
        int generation;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsDuplicateActiveLink(link))
            {
                EmitStatus("Duplicate WiFi Direct connection callback ignored for active session.");
                return;
            }

            StopCurrentSessionLocked("session_replaced", emitDisconnecting: _runtime != null || _activeLink != null);

            generation = unchecked(++_generation);
            sessionCancellationTokenSource = new CancellationTokenSource();
            _sessionCancellationTokenSource = sessionCancellationTokenSource;
            _activeLink = link;
            _lastDisconnectReason = null;
            TransitionTo(SessionPhase.WifiDirectConnected, link);
        }
        finally
        {
            _lifecycleLock.Release();
        }

        _ = Task.Run(
            () => RunSessionSetupAsync(link, generation, sessionCancellationTokenSource.Token),
            CancellationToken.None);
    }

    private async Task RunSessionSetupAsync(
        SessionLinkInfo link,
        int expectedGeneration,
        CancellationToken cancellationToken)
    {
        var openedTransports = new Dictionary<ProtocolChannel, ISessionTransport>();
        using var setupTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setupTimeoutCts.CancelAfter(SetupTimeout);
        var setupToken = setupTimeoutCts.Token;

        try
        {
            if (!IsCurrent(expectedGeneration))
            {
                return;
            }

            TransitionTo(SessionPhase.ConnectingTransport, link);
            foreach (var (channel, port) in ChannelPorts())
            {
                setupToken.ThrowIfCancellationRequested();
                var transport = link.Role == SessionRole.GroupOwner
                    ? await AcceptChannelAsync(channel, port, expectedGeneration, setupToken).ConfigureAwait(false)
                    : await ConnectChannelWithRetryAsync(channel, link.RemoteAddress, port, expectedGeneration, setupToken).ConfigureAwait(false);

                openedTransports[channel] = transport;
                EmitStatus($"{channel.GetProtocolName()} channel opened on port {port}");
            }

            var runtime = new SessionRuntime(expectedGeneration, link, openedTransports);
            TransitionTo(SessionPhase.Handshaking, link);
            await PerformHandshakeAsync(runtime, setupToken).ConfigureAwait(false);

            if (!IsCurrent(expectedGeneration))
            {
                runtime.Close();
                return;
            }

            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(expectedGeneration))
                {
                    runtime.Close();
                    return;
                }

                _runtime = runtime;
                _activeLink = link;
                TransitionTo(SessionPhase.Ready, link);
                EmitReady(runtime);
                StartControlReadLoop(runtime, cancellationToken);
                StartBulkReadLoop(runtime, cancellationToken);
                StartHeartbeat(runtime, cancellationToken);
                EmitStatus($"WDCable session ready ({link.Role.GetEventName()}, protocol v{ProtocolConstants.Version})");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        catch (PeerProtocolMissingException ex)
        {
            CloseTransports(openedTransports.Values);
            await FailSessionAsync(expectedGeneration, link, "peer_protocol_missing", ex, isPeerProtocolMissing: true).ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            CloseTransports(openedTransports.Values);
            var reason = ex.Error == ProtocolError.UnsupportedVersion
                ? "unsupported_protocol_version"
                : "protocol_error";
            await FailSessionAsync(
                expectedGeneration,
                link,
                reason,
                ex,
                isPeerProtocolMissing: ex.Error == ProtocolError.MalformedMagic).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            CloseTransports(openedTransports.Values);
            if (IsCurrent(expectedGeneration) && !cancellationToken.IsCancellationRequested)
            {
                await FailSessionAsync(expectedGeneration, link, "peer_protocol_missing", new PeerProtocolMissingException("Timed out waiting for WDCable transport setup", ex), true).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            CloseTransports(openedTransports.Values);
            await FailSessionAsync(expectedGeneration, link, "transport_setup_failed", ex, isPeerProtocolMissing: false).ConfigureAwait(false);
        }
    }

    private Task<ISessionTransport> AcceptChannelAsync(
        ProtocolChannel channel,
        int port,
        int expectedGeneration,
        CancellationToken cancellationToken)
    {
        var adapter = RequireTransportAdapter();
        var localAddress = ParseAddressOrAny(_activeLink?.LocalAddress);
        EmitStatus($"Accepting {channel.GetProtocolName()} channel on port {port}");
        return adapter.AcceptAsync(channel, localAddress, port, () => !IsCurrent(expectedGeneration), cancellationToken);
    }

    private async Task<ISessionTransport> ConnectChannelWithRetryAsync(
        ProtocolChannel channel,
        string host,
        int port,
        int expectedGeneration,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxConnectAttempts && IsCurrent(expectedGeneration); attempt++)
        {
            try
            {
                EmitStatus($"Connecting {channel.GetProtocolName()} channel to {host}:{port} (attempt {attempt}/{MaxConnectAttempts})");
                return await RequireTransportAdapter().ConnectAsync(channel, host, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (attempt < MaxConnectAttempts)
                {
                    await Task.Delay(ConnectRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (!IsCurrent(expectedGeneration))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw new PeerProtocolMissingException(
            $"Could not connect to WDCable {channel.GetProtocolName()} channel at {host}:{port}",
            lastError ?? new IOException("Connection attempts exhausted."));
    }

    private async Task PerformHandshakeAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HandshakeTimeout);

        try
        {
            if (runtime.Role == SessionRole.Client)
            {
                await runtime.ControlTransport.WriteFrameAsync(BuildHandshakeHello(runtime), timeoutCts.Token).ConfigureAwait(false);
                var ack = await runtime.ControlTransport.ReadFrameAsync(timeoutCts.Token).ConfigureAwait(false)
                    ?? throw new PeerProtocolMissingException("Peer closed before handshake ack");
                if (ack.Type != ProtocolFrameType.HandshakeAck)
                {
                    throw new PeerProtocolMissingException($"Expected handshake ack, received {ack.Type.GetProtocolName()}");
                }

                ValidateHandshakeAck(ack.MetadataJson);
            }
            else
            {
                var hello = await runtime.ControlTransport.ReadFrameAsync(timeoutCts.Token).ConfigureAwait(false)
                    ?? throw new PeerProtocolMissingException("Peer closed before handshake hello");
                if (hello.Type != ProtocolFrameType.HandshakeHello)
                {
                    throw new PeerProtocolMissingException($"Expected handshake hello, received {hello.Type.GetProtocolName()}");
                }

                ValidateHandshakeHello(hello.MetadataJson);
                await runtime.ControlTransport.WriteFrameAsync(BuildHandshakeAck(runtime), timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PeerProtocolMissingException("Timed out waiting for WDCable handshake", ex);
        }
        catch (JsonException ex)
        {
            throw new PeerProtocolMissingException("Invalid handshake metadata", ex);
        }
    }

    private ProtocolFrame BuildHandshakeHello(SessionRuntime runtime)
    {
        var metadata = BaseHandshakeMetadata(runtime);
        metadata["protocolMin"] = ProtocolConstants.Version;
        metadata["protocolMax"] = ProtocolConstants.Version;

        return new ProtocolFrame(
            ProtocolFrameType.HandshakeHello,
            ProtocolChannel.Control,
            sequenceNumber: runtime.NextSequenceNumber(),
            correlationId: Guid.NewGuid(),
            metadataJson: JsonSerializer.Serialize(metadata));
    }

    private ProtocolFrame BuildHandshakeAck(SessionRuntime runtime)
    {
        var metadata = BaseHandshakeMetadata(runtime);
        metadata["protocolVersion"] = ProtocolConstants.Version;

        return new ProtocolFrame(
            ProtocolFrameType.HandshakeAck,
            ProtocolChannel.Control,
            sequenceNumber: runtime.NextSequenceNumber(),
            correlationId: Guid.NewGuid(),
            metadataJson: JsonSerializer.Serialize(metadata));
    }

    private Dictionary<string, object?> BaseHandshakeMetadata(SessionRuntime runtime)
    {
        return new Dictionary<string, object?>
        {
            ["appId"] = ProtocolConstants.AppId,
            ["platform"] = "windows",
            ["appVersion"] = AppVersion(),
            ["deviceName"] = Environment.MachineName,
            ["role"] = runtime.Role.GetEventName(),
            ["sessionId"] = runtime.SessionId,
            ["capabilities"] = ProtocolConstants.AdvertisedCapabilities,
            ["channels"] = ChannelsMetadata()
        };
    }

    private static Dictionary<string, object> ChannelsMetadata()
    {
        return ChannelPorts().ToDictionary(
            pair => pair.Channel.GetProtocolName(),
            pair => (object)new Dictionary<string, object>
            {
                ["transport"] = "tcp",
                ["port"] = pair.Port
            });
    }

    private static void ValidateHandshakeHello(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var root = document.RootElement;
        ValidateAppId(root);

        var protocolMin = GetInt(root, "protocolMin", -1);
        var protocolMax = GetInt(root, "protocolMax", -1);
        if (ProtocolConstants.Version < protocolMin || ProtocolConstants.Version > protocolMax)
        {
            throw new ProtocolException(
                ProtocolError.UnsupportedVersion,
                $"Peer supports protocol {protocolMin}..{protocolMax}");
        }
    }

    private static void ValidateHandshakeAck(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var root = document.RootElement;
        ValidateAppId(root);

        var protocolVersion = GetInt(root, "protocolVersion", -1);
        if (protocolVersion != ProtocolConstants.Version)
        {
            throw new ProtocolException(
                ProtocolError.UnsupportedVersion,
                $"Peer selected unsupported protocol {protocolVersion}");
        }
    }

    private static void ValidateAppId(JsonElement root)
    {
        if (!root.TryGetProperty("appId", out var appId) || appId.GetString() != ProtocolConstants.AppId)
        {
            throw new PeerProtocolMissingException("Peer app id is not WDCable");
        }
    }

    private static int GetInt(JsonElement root, string propertyName, int fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : fallback;
    }

    private void StartControlReadLoop(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (IsCurrent(runtime.Generation) && CurrentPhase == SessionPhase.Ready && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var frame = await runtime.ControlTransport.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("Control channel closed by peer");
                    runtime.MarkFrameReceived();
                    await HandleControlFrameAsync(runtime, frame, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (IsCurrent(runtime.Generation) && CurrentPhase == SessionPhase.Ready)
                    {
                        await FailSessionAsync(runtime.Generation, runtime.Link, "control_channel_failed", ex, false).ConfigureAwait(false);
                    }

                    return;
                }
            }
        }, CancellationToken.None);
    }

    private void StartBulkReadLoop(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (IsCurrent(runtime.Generation) && CurrentPhase == SessionPhase.Ready && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var frame = await runtime.BulkTransport.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("Bulk channel closed by peer");
                    RaiseEvent(BulkFrameReceived, new ProtocolFrameReceivedEventArgs(runtime.SessionId, frame));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (IsCurrent(runtime.Generation) && CurrentPhase == SessionPhase.Ready)
                    {
                        await FailSessionAsync(runtime.Generation, runtime.Link, "bulk_channel_failed", ex, false).ConfigureAwait(false);
                    }

                    return;
                }
            }
        }, CancellationToken.None);
    }

    private void StartHeartbeat(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) &&
                       IsCurrent(runtime.Generation) &&
                       CurrentPhase == SessionPhase.Ready)
                {
                    var elapsed = DateTimeOffset.UtcNow - runtime.LastFrameReceivedAt;
                    if (elapsed > HeartbeatTimeout)
                    {
                        await FailSessionAsync(
                            runtime.Generation,
                            runtime.Link,
                            "heartbeat_timeout",
                            new TimeoutException("Timed out waiting for heartbeat response."),
                            false).ConfigureAwait(false);
                        return;
                    }

                    var ping = new ProtocolFrame(
                        ProtocolFrameType.HeartbeatPing,
                        ProtocolChannel.Control,
                        sequenceNumber: runtime.NextSequenceNumber(),
                        correlationId: Guid.NewGuid(),
                        metadataJson: "{}");
                    await runtime.ControlTransport.WriteFrameAsync(ping, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrent(runtime.Generation) && CurrentPhase == SessionPhase.Ready)
                {
                    await FailSessionAsync(runtime.Generation, runtime.Link, "heartbeat_failed", ex, false).ConfigureAwait(false);
                }
            }
        }, CancellationToken.None);
    }

    private async Task HandleControlFrameAsync(
        SessionRuntime runtime,
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case ProtocolFrameType.HeartbeatPing:
                var pong = new ProtocolFrame(
                    ProtocolFrameType.HeartbeatPong,
                    ProtocolChannel.Control,
                    sequenceNumber: runtime.NextSequenceNumber(),
                    correlationId: frame.CorrelationId == default ? Guid.NewGuid() : frame.CorrelationId,
                    metadataJson: "{}");
                await runtime.ControlTransport.WriteFrameAsync(pong, cancellationToken).ConfigureAwait(false);
                break;
            case ProtocolFrameType.HeartbeatPong:
                break;
            case ProtocolFrameType.Close:
                await DisconnectAsync("peer_closed").ConfigureAwait(false);
                break;
            case ProtocolFrameType.Error:
                EmitError($"Peer reported protocol error: {frame.MetadataJson}");
                RaiseEvent(ControlFrameReceived, new ProtocolFrameReceivedEventArgs(runtime.SessionId, frame));
                break;
            default:
                RaiseEvent(ControlFrameReceived, new ProtocolFrameReceivedEventArgs(runtime.SessionId, frame));
                break;
        }
    }

    private async Task FailSessionAsync(
        int expectedGeneration,
        SessionLinkInfo link,
        string reason,
        Exception exception,
        bool isPeerProtocolMissing)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrent(expectedGeneration))
            {
                return;
            }

            _lastDisconnectReason = reason;
            _sessionCancellationTokenSource?.Cancel();
            _transportAdapter?.Cancel();
            _runtime?.Close();
            _runtime = null;
            TransitionTo(SessionPhase.Failed, link, reason);

            var message = exception.Message;
            var args = new SessionFailedEventArgs(reason, message, link.SessionId, link.Role, isPeerProtocolMissing);
            RaiseEvent(SessionFailed, args);
            if (isPeerProtocolMissing)
            {
                RaiseEvent(PeerProtocolMissing, args);
            }

            RaiseEvent(DisconnectReasonChanged, new SessionDisconnectEventArgs(reason, link.SessionId));
            EmitError($"{reason}: {message}");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void StopCurrentSessionLocked(string reason, bool emitDisconnecting)
    {
        var hadSession = _runtime != null || _activeLink != null || CurrentPhase is not SessionPhase.Disconnected;
        var sessionId = CurrentSessionId;
        var link = _activeLink;

        if (!hadSession)
        {
            return;
        }

        _lastDisconnectReason = reason;
        _sessionCancellationTokenSource?.Cancel();
        _transportAdapter?.Cancel();
        _runtime?.Close();
        _runtime = null;
        _activeLink = null;
        _sessionCancellationTokenSource?.Dispose();
        _sessionCancellationTokenSource = null;

        if (emitDisconnecting)
        {
            TransitionTo(SessionPhase.Disconnecting, link, reason);
        }

        TransitionTo(SessionPhase.Disconnected, link, reason);
        RaiseEvent(DisconnectReasonChanged, new SessionDisconnectEventArgs(reason, sessionId));
        EmitStatus($"Session disconnected: {reason}");

        _transportAdapter?.Dispose();
        _transportAdapter = new TcpSessionTransportAdapter();
    }

    private void TransitionTo(SessionPhase phase, SessionLinkInfo? link, string? reason = null)
    {
        lock (_stateLock)
        {
            try
            {
                _stateMachine.TransitionTo(phase);
            }
            catch (InvalidOperationException)
            {
                _stateMachine.Reset(phase);
            }
        }

        RaiseEvent(
            StateChanged,
            new SessionStateChangedEventArgs(
                phase,
                link?.SessionId ?? CurrentSessionId,
                link?.Role ?? CurrentRole,
                link?.PeerName ?? PeerName,
                link?.RemoteAddress ?? PeerAddress,
                reason ?? _lastDisconnectReason));
    }

    private void EmitReady(SessionRuntime runtime)
    {
        RaiseEvent(
            SessionReady,
            new SessionReadyEventArgs(
                runtime.SessionId,
                runtime.Role,
                runtime.PeerName,
                runtime.PeerAddress,
                ProtocolConstants.Version,
                ProtocolConstants.AdvertisedCapabilities));
    }

    private void EmitStatus(string status)
    {
        RaiseEvent(StatusChanged, status);
    }

    private void EmitError(string error)
    {
        RaiseEvent(ErrorOccurred, error);
    }

    private void RaiseEvent<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null)
        {
            return;
        }

        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => handler(this, args));
        }
        else
        {
            handler(this, args);
        }
    }

    private SessionRuntime RequireReadyRuntime()
    {
        return IsReady && _runtime != null
            ? _runtime
            : throw new InvalidOperationException("WDCable session is not Ready.");
    }

    private ISessionTransportAdapter RequireTransportAdapter()
    {
        return _transportAdapter ?? throw new ObjectDisposedException(nameof(SessionManager));
    }

    private bool IsCurrent(int expectedGeneration)
    {
        return Volatile.Read(ref _generation) == expectedGeneration;
    }

    private bool IsDuplicateActiveLink(SessionLinkInfo link)
    {
        if (_activeLink == null)
        {
            return false;
        }

        var phase = CurrentPhase;
        if (phase is SessionPhase.Disconnected or SessionPhase.Failed)
        {
            return false;
        }

        return _activeLink.PeerId == link.PeerId &&
               _activeLink.Role == link.Role &&
               _activeLink.LocalAddress == link.LocalAddress &&
               _activeLink.RemoteAddress == link.RemoteAddress;
    }

    private static SessionLinkInfo BuildLinkInfo(WiFiDirectService wifiDirectService, WiFiDirectDevice device)
    {
        if (!wifiDirectService.IsConnected)
        {
            throw new InvalidOperationException("WiFi Direct is not connected.");
        }

        if (string.IsNullOrWhiteSpace(wifiDirectService.LocalIP))
        {
            throw new InvalidOperationException("Local WiFi Direct IP is not available.");
        }

        if (string.IsNullOrWhiteSpace(wifiDirectService.RemoteIP))
        {
            throw new InvalidOperationException("Remote WiFi Direct IP is not available.");
        }

        return new SessionLinkInfo(
            Guid.NewGuid().ToString(),
            device.Id,
            device.Name,
            wifiDirectService.IsGroupOwner ? SessionRole.GroupOwner : SessionRole.Client,
            wifiDirectService.LocalIP,
            wifiDirectService.RemoteIP);
    }

    private static IPAddress ParseAddressOrAny(string? address)
    {
        return IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Any;
    }

    private static IReadOnlyList<(ProtocolChannel Channel, int Port)> ChannelPorts()
    {
        return
        [
            (ProtocolChannel.Control, ProtocolConstants.DefaultControlPort),
            (ProtocolChannel.Bulk, ProtocolConstants.DefaultBulkPort)
        ];
    }

    private static TimeSpan ConnectRetryDelay(int attempt)
    {
        var milliseconds = InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxRetryDelay.TotalMilliseconds));
    }

    private static string AppVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void CloseTransports(IEnumerable<ISessionTransport> transports)
    {
        foreach (var transport in transports)
        {
            try
            {
                transport.Cancel();
            }
            catch
            {
            }
        }
    }

    private sealed record SessionLinkInfo(
        string SessionId,
        string PeerId,
        string PeerName,
        SessionRole Role,
        string LocalAddress,
        string RemoteAddress);

    private sealed class SessionRuntime
    {
        private long _sequenceNumber;
        private long _lastFrameReceivedAtTicks;

        public SessionRuntime(
            int generation,
            SessionLinkInfo link,
            IReadOnlyDictionary<ProtocolChannel, ISessionTransport> transports)
        {
            Generation = generation;
            Link = link;
            Transports = transports;
            MarkFrameReceived();
        }

        public int Generation { get; }

        public SessionLinkInfo Link { get; }

        public string SessionId => Link.SessionId;

        public SessionRole Role => Link.Role;

        public string PeerName => Link.PeerName;

        public string PeerAddress => Link.RemoteAddress;

        public IReadOnlyDictionary<ProtocolChannel, ISessionTransport> Transports { get; }

        public ISessionTransport ControlTransport => Transports[ProtocolChannel.Control];

        public ISessionTransport BulkTransport => Transports[ProtocolChannel.Bulk];

        public DateTimeOffset LastFrameReceivedAt =>
            new DateTimeOffset(Interlocked.Read(ref _lastFrameReceivedAtTicks), TimeSpan.Zero);

        public long NextSequenceNumber()
        {
            return Interlocked.Increment(ref _sequenceNumber);
        }

        public void MarkFrameReceived()
        {
            Interlocked.Exchange(ref _lastFrameReceivedAtTicks, DateTimeOffset.UtcNow.Ticks);
        }

        public void Close()
        {
            foreach (var transport in Transports.Values)
            {
                try
                {
                    transport.Cancel();
                }
                catch
                {
                }
            }
        }
    }
}
