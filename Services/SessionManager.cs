using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WDCableWUI.Protocol;
using Windows.ApplicationModel;

namespace WDCableWUI.Services;

public sealed class SessionManager : IDisposable
{
    private const int BulkChunkSize = 64 * 1024;
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RendezvousSendInterval = TimeSpan.FromMilliseconds(500);
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
    private readonly object _audioTransportLock = new();

    private WiFiDirectService? _wifiDirectService;
    private ISessionTransportAdapter? _transportAdapter;
    private SessionRuntime? _runtime;
    private ISessionTransportListener? _audioListener;
    private ISessionTransport? _audioTransport;
    private long _audioStreamId;
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
    public event EventHandler<ProtocolFrameReceivedEventArgs>? AudioFrameReceived;
    public event EventHandler<AudioTransportEventArgs>? AudioTransportReady;
    public event EventHandler<AudioTransportClosedEventArgs>? AudioTransportClosed;

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

    public SessionTransportRole? CurrentTransportRole => _runtime?.TransportRole ?? _activeLink?.TransportRole;

    public string? PeerName => _runtime?.PeerName ?? _activeLink?.PeerName;

    public string? PeerAddress => _runtime?.PeerAddress ?? _activeLink?.RemoteAddress;

    public IReadOnlyList<string> PeerCapabilities => _runtime?.PeerCapabilities ?? [];

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

    public AudioSessionInfo? GetAudioSessionInfo()
    {
        var runtime = IsReady ? _runtime : null;
        return runtime == null
            ? null
            : new AudioSessionInfo(
                runtime.SessionId,
                runtime.Role,
                runtime.PeerAddress,
                runtime.PeerCapabilities);
    }

    public async Task SendAudioControlAsync(
        string metadataJson,
        long streamId = 0,
        CancellationToken cancellationToken = default)
    {
        await SendControlFrameAsync(
            ProtocolFrameType.ControlMessage,
            metadataJson,
            streamId: streamId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAudioFeatureErrorAsync(
        long streamId,
        string code,
        string message,
        CancellationToken cancellationToken = default)
    {
        await SendControlFrameAsync(
            ProtocolFrameType.Error,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
                ["streamId"] = streamId
            }),
            streamId: streamId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<int> StartAudioListenerAsync(
        long streamId,
        CancellationToken cancellationToken = default)
    {
        var runtime = RequireReadyRuntime();
        if (runtime.Role != SessionRole.GroupOwner)
        {
            throw new InvalidOperationException("Only the group owner can listen for the audio channel.");
        }

        CloseAudioTransport();
        var localAddress = ParseAddressOrAny(runtime.Link.LocalAddress);
        var listener = RequireTransportAdapter().Listen(ProtocolChannel.Audio, localAddress, 0);
        lock (_audioTransportLock)
        {
            _audioStreamId = streamId;
            _audioListener = listener;
        }

        var acceptToken = CreateLinkedAudioToken(cancellationToken);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var transport = await listener.AcceptAsync(() => !IsCurrent(runtime.Generation), acceptToken.Token).ConfigureAwait(false);
                    lock (_audioTransportLock)
                    {
                        if (_audioListener == listener)
                        {
                            _audioListener = null;
                        }

                        _audioTransport = transport;
                        _audioStreamId = streamId;
                    }

                    StartAudioReadLoop(runtime, transport, streamId);
                    RaiseEvent(AudioTransportReady, new AudioTransportEventArgs(runtime.SessionId, streamId));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var stillCurrent = IsCurrent(runtime.Generation);
                    lock (_audioTransportLock)
                    {
                        if (_audioListener == listener)
                        {
                            _audioListener = null;
                        }
                    }

                    if (stillCurrent)
                    {
                        RaiseEvent(AudioTransportClosed, new AudioTransportClosedEventArgs(runtime.SessionId, streamId, ex.Message));
                    }
                }
                finally
                {
                    acceptToken.Dispose();
                }
            },
            CancellationToken.None);

        return Task.FromResult(listener.Port);
    }

    public async Task ConnectAudioTransportAsync(
        long streamId,
        int port,
        CancellationToken cancellationToken = default)
    {
        var runtime = RequireReadyRuntime();
        if (runtime.Role != SessionRole.Client)
        {
            throw new InvalidOperationException("Only the client connects to the audio channel.");
        }

        if (port <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Audio port must be positive.");
        }

        CloseAudioTransport();
        using var connectToken = CreateLinkedAudioToken(cancellationToken);
        var transport = await ConnectChannelWithRetryAsync(
            ProtocolChannel.Audio,
            runtime.PeerAddress,
            port,
            runtime.Generation,
            connectToken.Token).ConfigureAwait(false);

        lock (_audioTransportLock)
        {
            _audioTransport = transport;
            _audioStreamId = streamId;
        }

        StartAudioReadLoop(runtime, transport, streamId);
        RaiseEvent(AudioTransportReady, new AudioTransportEventArgs(runtime.SessionId, streamId));
    }

    public async Task WriteAudioFrameAsync(
        long streamId,
        long sequenceNumber,
        string metadataJson,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ISessionTransport? transport;
        lock (_audioTransportLock)
        {
            transport = _audioTransport;
        }

        if (transport == null)
        {
            throw new IOException("Audio channel is not connected.");
        }

        var runtime = RequireReadyRuntime();
        var frame = new ProtocolFrame(
            ProtocolFrameType.AudioFrame,
            ProtocolChannel.Audio,
            streamId: streamId,
            sequenceNumber: sequenceNumber,
            correlationId: Guid.NewGuid(),
            metadataJson: metadataJson,
            payload: payload);

        await transport.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public void CloseAudioTransport()
    {
        ISessionTransportListener? listener;
        ISessionTransport? transport;
        lock (_audioTransportLock)
        {
            listener = _audioListener;
            transport = _audioTransport;
            _audioListener = null;
            _audioTransport = null;
            _audioStreamId = 0;
        }

        try
        {
            listener?.Dispose();
        }
        catch
        {
        }

        try
        {
            transport?.Cancel();
            transport?.Dispose();
        }
        catch
        {
        }
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
        CloseAudioTransport();
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
            openedTransports = link.TransportRole == SessionTransportRole.Listener
                ? await OpenListenerTransportsAsync(link, expectedGeneration, setupToken).ConfigureAwait(false)
                : await OpenConnectorTransportsAsync(link, expectedGeneration, setupToken).ConfigureAwait(false);

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
                EmitStatus($"WDCable session ready ({link.Role.GetEventName()}/{link.TransportRole.GetEventName()}, protocol v{ProtocolConstants.Version})");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        catch (SessionSetupException ex)
        {
            CloseTransports(openedTransports.Values);
            await FailSessionAsync(expectedGeneration, link, ex.Reason, ex, ex.IsPeerProtocolMissing).ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            CloseTransports(openedTransports.Values);
            await FailSessionAsync(
                expectedGeneration,
                link,
                "protocol_mismatch",
                ex,
                isPeerProtocolMissing: ex.Error is ProtocolError.MalformedMagic or
                    ProtocolError.UnsupportedVersion or
                    ProtocolError.ProtocolMismatch).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            CloseTransports(openedTransports.Values);
            if (IsCurrent(expectedGeneration) && !cancellationToken.IsCancellationRequested)
            {
                var reason = link.TransportRole == SessionTransportRole.Listener
                    ? "udp_rendezvous_timeout"
                    : "tcp_connect_timeout";
                await FailSessionAsync(expectedGeneration, link, reason, new TimeoutException("Timed out waiting for WDCable transport setup", ex), false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            CloseTransports(openedTransports.Values);
            var reason = link.TransportRole == SessionTransportRole.Listener
                ? "bind_failed"
                : "tcp_connect_timeout";
            await FailSessionAsync(expectedGeneration, link, reason, ex, isPeerProtocolMissing: false).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<ProtocolChannel, ISessionTransport>> OpenListenerTransportsAsync(
        SessionLinkInfo link,
        int expectedGeneration,
        CancellationToken cancellationToken)
    {
        var transports = new Dictionary<ProtocolChannel, ISessionTransport>();
        var listeners = new Dictionary<ProtocolChannel, ISessionTransportListener>();
        var acceptTasks = new Dictionary<ProtocolChannel, Task<ISessionTransport>>();

        try
        {
            var localAddress = ParseRequiredAddress(link.LocalAddress, "bind_failed");
            foreach (var (channel, port) in ChannelPorts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                EmitStatus($"Binding TCP {channel.GetProtocolName()} listener on {localAddress}:{port}");
                try
                {
                    var listener = RequireTransportAdapter().Listen(channel, localAddress, port);
                    listeners[channel] = listener;
                    EmitStatus($"Bound TCP {channel.GetProtocolName()} listener on {localAddress}:{listener.Port}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new SessionSetupException(
                        "bind_failed",
                        $"Could not bind {channel.GetProtocolName()} listener on {localAddress}:{port}",
                        ex);
                }
            }

            foreach (var (channel, listener) in listeners)
            {
                acceptTasks[channel] = listener.AcceptAsync(() => !IsCurrent(expectedGeneration), cancellationToken);
                EmitStatus($"Accepting TCP {channel.GetProtocolName()} channel on {link.LocalAddress}:{listener.Port}");
            }

            using var rendezvousCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var rendezvousTask = SendUdpRendezvousUntilControlAcceptedAsync(
                link,
                acceptTasks[ProtocolChannel.Control],
                rendezvousCts.Token);

            try
            {
                foreach (var (channel, port) in ChannelPorts())
                {
                    var transport = await acceptTasks[channel].ConfigureAwait(false);
                    transports[channel] = transport;
                    EmitStatus($"{channel.GetProtocolName()} channel accepted on port {port}");

                    if (channel == ProtocolChannel.Control)
                    {
                        rendezvousCts.Cancel();
                    }
                }

                return transports;
            }
            finally
            {
                rendezvousCts.Cancel();
                await ObserveTaskQuietlyAsync(rendezvousTask).ConfigureAwait(false);
            }
        }
        catch
        {
            CloseTransports(transports.Values);
            throw;
        }
        finally
        {
            foreach (var listener in listeners.Values)
            {
                try
                {
                    listener.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private async Task<Dictionary<ProtocolChannel, ISessionTransport>> OpenConnectorTransportsAsync(
        SessionLinkInfo link,
        int expectedGeneration,
        CancellationToken cancellationToken)
    {
        var transports = new Dictionary<ProtocolChannel, ISessionTransport>();
        try
        {
            foreach (var (channel, port) in ChannelPorts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transport = await ConnectChannelWithRetryAsync(
                    channel,
                    link.RemoteAddress,
                    port,
                    expectedGeneration,
                    cancellationToken).ConfigureAwait(false);
                transports[channel] = transport;
                EmitStatus($"{channel.GetProtocolName()} channel connected to {link.RemoteAddress}:{port}");
            }

            return transports;
        }
        catch
        {
            CloseTransports(transports.Values);
            throw;
        }
    }

    private async Task<ISessionTransport> ConnectChannelWithRetryAsync(
        ProtocolChannel channel,
        string host,
        int port,
        int expectedGeneration,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var attempt = 0;
        while (IsCurrent(expectedGeneration) && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                EmitStatus($"Connecting {channel.GetProtocolName()} channel to {host}:{port} (attempt {attempt})");
                return await RequireTransportAdapter().ConnectAsync(channel, host, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                EmitStatus($"TCP {channel.GetProtocolName()} connect attempt {attempt} failed: {ex.Message}");
                await Task.Delay(ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!IsCurrent(expectedGeneration))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw new OperationCanceledException(
            $"Timed out connecting WDCable {channel.GetProtocolName()} channel at {host}:{port}",
            lastError,
            cancellationToken);
    }

    private async Task SendUdpRendezvousUntilControlAcceptedAsync(
        SessionLinkInfo link,
        Task<ISessionTransport> controlAcceptTask,
        CancellationToken cancellationToken)
    {
        var localAddress = ParseRequiredAddress(link.LocalAddress, "bind_failed");
        var remoteAddress = ParseRequiredAddress(link.RemoteAddress, "endpoint_unavailable");
        var remoteEndPoint = new IPEndPoint(remoteAddress, ProtocolConstants.DefaultRendezvousPort);
        var localEndPoint = new IPEndPoint(localAddress, 0);
        var successLogged = false;

        EmitStatus($"UDP rendezvous send started to {remoteEndPoint}");

        using var udpClient = new UdpClient(localEndPoint);
        while (!controlAcceptTask.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var payload = SessionRendezvousPayload.Build(
                    link.RendezvousId,
                    link.Role,
                    link.TransportRole);
                var bytes = Encoding.UTF8.GetBytes(payload);
                await udpClient.SendAsync(bytes, bytes.Length, remoteEndPoint)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!successLogged)
                {
                    successLogged = true;
                    EmitStatus($"UDP rendezvous packet sent to {remoteEndPoint}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EmitError($"UDP rendezvous send failed: {ex.Message}");
            }

            await Task.Delay(RendezvousSendInterval, cancellationToken).ConfigureAwait(false);
        }
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
                    ?? throw new SessionSetupException(
                        "protocol_mismatch",
                        "Peer closed before handshake ack",
                        isPeerProtocolMissing: true);
                if (ack.Type != ProtocolFrameType.HandshakeAck)
                {
                    throw new SessionSetupException(
                        "protocol_mismatch",
                        $"Expected handshake ack, received {ack.Type.GetProtocolName()}",
                        isPeerProtocolMissing: true);
                }

                runtime.SetPeerCapabilities(SessionHandshakeMetadata.ValidateAck(
                    ack.MetadataJson,
                    runtime.Role.GetPeerRole(),
                    runtime.TransportRole.GetPeerRole()));
            }
            else
            {
                var hello = await runtime.ControlTransport.ReadFrameAsync(timeoutCts.Token).ConfigureAwait(false)
                    ?? throw new SessionSetupException(
                        "protocol_mismatch",
                        "Peer closed before handshake hello",
                        isPeerProtocolMissing: true);
                if (hello.Type != ProtocolFrameType.HandshakeHello)
                {
                    throw new SessionSetupException(
                        "protocol_mismatch",
                        $"Expected handshake hello, received {hello.Type.GetProtocolName()}",
                        isPeerProtocolMissing: true);
                }

                runtime.SetPeerCapabilities(SessionHandshakeMetadata.ValidateHello(
                    hello.MetadataJson,
                    runtime.Role.GetPeerRole(),
                    runtime.TransportRole.GetPeerRole()));
                await runtime.ControlTransport.WriteFrameAsync(BuildHandshakeAck(runtime), timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionSetupException("handshake_timeout", "Timed out waiting for WDCable handshake", ex);
        }
        catch (JsonException ex)
        {
            throw new SessionSetupException(
                "protocol_mismatch",
                "Invalid handshake metadata",
                ex,
                isPeerProtocolMissing: true);
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
        return SessionHandshakeMetadata.BuildBase(
            "windows",
            AppVersion(),
            Environment.MachineName,
            runtime.Role,
            runtime.TransportRole,
            runtime.SessionId,
            ChannelsMetadata());
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

    private static bool IsAudioFeatureError(string metadataJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(metadataJson))
            {
                return false;
            }

            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.String &&
                   (code.GetString()?.StartsWith("audio_", StringComparison.Ordinal) ?? false);
        }
        catch (JsonException)
        {
            return false;
        }
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

    private void StartAudioReadLoop(SessionRuntime runtime, ISessionTransport transport, long streamId)
    {
        var cancellationToken = _sessionCancellationTokenSource?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            while (IsCurrent(runtime.Generation) && CurrentPhase == SessionPhase.Ready && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var frame = await transport.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("Audio channel closed by peer");
                    runtime.MarkFrameReceived();

                    if (frame.Type == ProtocolFrameType.AudioFrame && frame.Channel == ProtocolChannel.Audio)
                    {
                        RaiseEvent(AudioFrameReceived, new ProtocolFrameReceivedEventArgs(runtime.SessionId, frame));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var wasActive = false;
                    lock (_audioTransportLock)
                    {
                        if (_audioTransport == transport)
                        {
                            _audioTransport = null;
                            _audioStreamId = 0;
                            wasActive = true;
                        }
                    }

                    try
                    {
                        transport.Cancel();
                    }
                    catch
                    {
                    }

                    if (wasActive && IsCurrent(runtime.Generation) && CurrentPhase == SessionPhase.Ready)
                    {
                        RaiseEvent(
                            AudioTransportClosed,
                            new AudioTransportClosedEventArgs(runtime.SessionId, streamId, ex.Message));
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
                if (!IsAudioFeatureError(frame.MetadataJson))
                {
                    EmitError($"Peer reported protocol error: {frame.MetadataJson}");
                }

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
            var sessionCancellationTokenSource = _sessionCancellationTokenSource;
            _sessionCancellationTokenSource = null;
            sessionCancellationTokenSource?.Cancel();
            CloseAudioTransport();
            _transportAdapter?.Cancel();
            _transportAdapter?.Dispose();
            _transportAdapter = new TcpSessionTransportAdapter();
            _runtime?.Close();
            _runtime = null;
            sessionCancellationTokenSource?.Dispose();
            TransitionTo(SessionPhase.Failed, link, reason);

            var message = exception.Message;
            var args = new SessionFailedEventArgs(reason, message, link.SessionId, link.Role, link.TransportRole, isPeerProtocolMissing);
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
        CloseAudioTransport();
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
                link?.TransportRole ?? CurrentTransportRole,
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
                runtime.TransportRole,
                runtime.PeerName,
                runtime.PeerAddress,
                ProtocolConstants.Version,
                ProtocolConstants.AdvertisedCapabilities,
                runtime.PeerCapabilities));
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

    private CancellationTokenSource CreateLinkedAudioToken(CancellationToken cancellationToken)
    {
        var sessionToken = _sessionCancellationTokenSource?.Token ?? CancellationToken.None;
        return CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
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
               _activeLink.TransportRole == link.TransportRole &&
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

        var role = wifiDirectService.IsGroupOwner ? SessionRole.GroupOwner : SessionRole.Client;
        var transportRole = wifiDirectService.TransportRole ?? role.GetTransportRole();
        return new SessionLinkInfo(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            device.Id,
            device.Name,
            role,
            transportRole,
            wifiDirectService.LocalIP,
            wifiDirectService.RemoteIP);
    }

    private static IPAddress ParseAddressOrAny(string? address)
    {
        return IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Any;
    }

    private static IPAddress ParseRequiredAddress(string address, string reason)
    {
        if (IPAddress.TryParse(address, out var parsed))
        {
            return parsed;
        }

        throw new SessionSetupException(reason, $"Invalid IP address: {address}");
    }

    private static IReadOnlyList<(ProtocolChannel Channel, int Port)> ChannelPorts()
    {
        return
        [
            (ProtocolChannel.Control, ProtocolConstants.DefaultControlPort),
            (ProtocolChannel.Bulk, ProtocolConstants.DefaultBulkPort)
        ];
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
                transport.Dispose();
            }
            catch
            {
            }
        }
    }

    private static async Task ObserveTaskQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed class SessionSetupException : IOException
    {
        public SessionSetupException(
            string reason,
            string message,
            Exception? innerException = null,
            bool isPeerProtocolMissing = false)
            : base(message, innerException)
        {
            Reason = reason;
            IsPeerProtocolMissing = isPeerProtocolMissing;
        }

        public string Reason { get; }

        public bool IsPeerProtocolMissing { get; }
    }

    private sealed record SessionLinkInfo(
        string SessionId,
        string RendezvousId,
        string PeerId,
        string PeerName,
        SessionRole Role,
        SessionTransportRole TransportRole,
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

        public SessionTransportRole TransportRole => Link.TransportRole;

        public string PeerName => Link.PeerName;

        public string PeerAddress => Link.RemoteAddress;

        public IReadOnlyDictionary<ProtocolChannel, ISessionTransport> Transports { get; }

        public IReadOnlyList<string> PeerCapabilities { get; private set; } = [];

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

        public void SetPeerCapabilities(IReadOnlyList<string> peerCapabilities)
        {
            PeerCapabilities = peerCapabilities;
        }

        public void Close()
        {
            foreach (var transport in Transports.Values)
            {
                try
                {
                    transport.Cancel();
                    transport.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
