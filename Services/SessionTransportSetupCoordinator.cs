using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

internal sealed class SessionTransportSetupOptions
{
    public int MaxConnectAttempts { get; init; } = 10;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan LegacyAttemptTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan FallbackBackoff { get; init; } = TimeSpan.FromMilliseconds(750);

    public TimeSpan SymmetricProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan SymmetricPreferredCandidateGrace { get; init; } = TimeSpan.FromMilliseconds(750);
}

internal sealed class SessionTransportSetupException : IOException
{
    public SessionTransportSetupException(string message)
        : base(message)
    {
    }

    public SessionTransportSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class SessionTransportSetupCoordinator
{
    private const string TransportProbeKind = "session.transport.probe";

    private readonly Func<ISessionTransportAdapter> _transportAdapterProvider;
    private readonly Action _resetTransportAdapter;
    private readonly Action<string>? _emitStatus;
    private readonly SessionTransportSetupOptions _options;

    public SessionTransportSetupCoordinator(
        ISessionTransportAdapter transportAdapter,
        Action<string>? emitStatus = null,
        SessionTransportSetupOptions? options = null)
        : this(() => transportAdapter, () => { }, emitStatus, options)
    {
    }

    public SessionTransportSetupCoordinator(
        Func<ISessionTransportAdapter> transportAdapterProvider,
        Action resetTransportAdapter,
        Action<string>? emitStatus = null,
        SessionTransportSetupOptions? options = null)
    {
        _transportAdapterProvider = transportAdapterProvider;
        _resetTransportAdapter = resetTransportAdapter;
        _emitStatus = emitStatus;
        _options = options ?? new SessionTransportSetupOptions();
    }

    public async Task<IReadOnlyDictionary<ProtocolChannel, ISessionTransport>> OpenWithFallbackAsync(
        SessionRole role,
        string localAddress,
        string remoteAddress,
        IReadOnlyList<(ProtocolChannel Channel, int Port)> channels,
        Func<bool> shouldCancel,
        CancellationToken cancellationToken)
    {
        try
        {
            using var legacyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            legacyTimeout.CancelAfter(_options.LegacyAttemptTimeout);
            return await OpenLegacyAsync(
                role,
                localAddress,
                remoteAddress,
                channels,
                shouldCancel,
                legacyTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryWithSymmetric(ex, cancellationToken))
        {
            _emitStatus?.Invoke($"Legacy WDCable transport setup failed: {ex.Message}. Retrying with symmetric setup.");
            _resetTransportAdapter();
            await Task.Delay(_options.FallbackBackoff, cancellationToken).ConfigureAwait(false);
            return await OpenSymmetricAsync(
                localAddress,
                remoteAddress,
                channels,
                shouldCancel,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<ProtocolChannel, ISessionTransport>> OpenLegacyAsync(
        SessionRole role,
        string localAddress,
        string remoteAddress,
        IReadOnlyList<(ProtocolChannel Channel, int Port)> channels,
        Func<bool> shouldCancel,
        CancellationToken cancellationToken)
    {
        var openedTransports = new Dictionary<ProtocolChannel, ISessionTransport>();
        try
        {
            foreach (var (channel, port) in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transport = role == SessionRole.GroupOwner
                    ? await AcceptLegacyChannelAsync(channel, localAddress, port, shouldCancel, cancellationToken).ConfigureAwait(false)
                    : await ConnectChannelWithRetryAsync(channel, remoteAddress, port, cancellationToken).ConfigureAwait(false);

                openedTransports[channel] = transport;
                _emitStatus?.Invoke($"{channel.GetProtocolName()} channel opened on port {port}");
            }

            return openedTransports;
        }
        catch
        {
            CloseTransports(openedTransports.Values);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<ProtocolChannel, ISessionTransport>> OpenSymmetricAsync(
        string localAddress,
        string remoteAddress,
        IReadOnlyList<(ProtocolChannel Channel, int Port)> channels,
        Func<bool> shouldCancel,
        CancellationToken cancellationToken)
    {
        var openedTransports = new Dictionary<ProtocolChannel, ISessionTransport>();
        try
        {
            foreach (var (channel, port) in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transport = await OpenSymmetricChannelAsync(
                    channel,
                    localAddress,
                    remoteAddress,
                    port,
                    shouldCancel,
                    cancellationToken).ConfigureAwait(false);

                openedTransports[channel] = transport;
                _emitStatus?.Invoke($"{channel.GetProtocolName()} channel opened on port {port} using symmetric setup");
            }

            return openedTransports;
        }
        catch
        {
            CloseTransports(openedTransports.Values);
            throw;
        }
    }

    private async Task<ISessionTransport> AcceptLegacyChannelAsync(
        ProtocolChannel channel,
        string localAddress,
        int port,
        Func<bool> shouldCancel,
        CancellationToken cancellationToken)
    {
        try
        {
            var adapter = _transportAdapterProvider();
            var parsedLocalAddress = ParseAddressOrAny(localAddress);
            _emitStatus?.Invoke($"Accepting {channel.GetProtocolName()} channel on port {port}");
            return await adapter.AcceptAsync(channel, parsedLocalAddress, port, shouldCancel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SessionTransportSetupException(
                $"Could not accept WDCable {channel.GetProtocolName()} channel on port {port}",
                ex);
        }
    }

    private async Task<ISessionTransport> ConnectChannelWithRetryAsync(
        ProtocolChannel channel,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= _options.MaxConnectAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _emitStatus?.Invoke($"Connecting {channel.GetProtocolName()} channel to {host}:{port} (attempt {attempt}/{_options.MaxConnectAttempts})");
                return await _transportAdapterProvider().ConnectAsync(channel, host, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (attempt < _options.MaxConnectAttempts)
                {
                    await Task.Delay(ConnectRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new SessionTransportSetupException(
            $"Could not connect to WDCable {channel.GetProtocolName()} channel at {host}:{port}",
            lastError ?? new IOException("Connection attempts exhausted."));
    }

    private async Task<ISessionTransport> OpenSymmetricChannelAsync(
        ProtocolChannel channel,
        string localAddress,
        string remoteAddress,
        int port,
        Func<bool> shouldCancel,
        CancellationToken cancellationToken)
    {
        ISessionTransportListener? listener = null;
        CancellationTokenSource? channelCancellation = null;
        try
        {
            listener = _transportAdapterProvider().Listen(channel, ParseAddressOrAny(localAddress), port);
            channelCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var candidateToken = channelCancellation.Token;

            _emitStatus?.Invoke($"Symmetric setup listening for {channel.GetProtocolName()} channel on port {port}");
            var inboundTask = AcceptValidatedCandidateAsync(
                listener,
                channel,
                remoteAddress,
                shouldCancel,
                candidateToken);
            var outboundTask = ConnectValidatedCandidateWithRetryAsync(
                channel,
                remoteAddress,
                port,
                candidateToken);

            var preferOutbound = PreferOutbound(localAddress, remoteAddress);
            var selected = await SelectSymmetricCandidateAsync(
                channel,
                inboundTask,
                outboundTask,
                preferOutbound,
                cancellationToken).ConfigureAwait(false);

            channelCancellation.Cancel();
            listener.Close();
            CloseUnselectedOnCompletion(inboundTask, selected.Transport);
            CloseUnselectedOnCompletion(outboundTask, selected.Transport);
            return selected.Transport;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PeerProtocolMissingException and not ProtocolException and not SessionTransportSetupException)
        {
            throw new SessionTransportSetupException(
                $"Could not open symmetric WDCable {channel.GetProtocolName()} channel on port {port}",
                ex);
        }
        finally
        {
            try
            {
                listener?.Close();
                listener?.Dispose();
            }
            catch
            {
            }

            channelCancellation?.Dispose();
        }
    }

    private async Task<TransportCandidate> AcceptValidatedCandidateAsync(
        ISessionTransportListener listener,
        ProtocolChannel channel,
        string remoteAddress,
        Func<bool> shouldCancel,
        CancellationToken cancellationToken)
    {
        while (!shouldCancel())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ISessionTransport? transport = null;
            try
            {
                transport = await listener.AcceptAsync(shouldCancel, cancellationToken).ConfigureAwait(false);
                if (!IsExpectedRemote(transport, remoteAddress))
                {
                    _emitStatus?.Invoke($"Rejected inbound {channel.GetProtocolName()} candidate from unexpected endpoint {transport.RemoteEndPoint}");
                    CloseTransport(transport);
                    continue;
                }

                await ValidateSymmetricProbeAsync(transport, channel, cancellationToken).ConfigureAwait(false);
                return new TransportCandidate(transport, CandidateDirection.Inbound);
            }
            catch
            {
                if (transport != null)
                {
                    CloseTransport(transport);
                }

                throw;
            }
        }

        throw new OperationCanceledException($"Accept cancelled for {channel.GetProtocolName()} channel.", cancellationToken);
    }

    private async Task<TransportCandidate> ConnectValidatedCandidateWithRetryAsync(
        ProtocolChannel channel,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= _options.MaxConnectAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ISessionTransport? transport = null;
            try
            {
                _emitStatus?.Invoke($"Symmetric setup connecting {channel.GetProtocolName()} channel to {host}:{port} (attempt {attempt}/{_options.MaxConnectAttempts})");
                transport = await _transportAdapterProvider().ConnectAsync(channel, host, port, cancellationToken).ConfigureAwait(false);
                if (!IsExpectedRemote(transport, host))
                {
                    throw new SessionTransportSetupException(
                        $"Connected {channel.GetProtocolName()} channel to unexpected endpoint {transport.RemoteEndPoint}");
                }

                await ValidateSymmetricProbeAsync(transport, channel, cancellationToken).ConfigureAwait(false);
                return new TransportCandidate(transport, CandidateDirection.Outbound);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (transport != null)
                {
                    CloseTransport(transport);
                }

                if (ex is PeerProtocolMissingException or ProtocolException)
                {
                    throw;
                }

                if (attempt < _options.MaxConnectAttempts)
                {
                    await Task.Delay(ConnectRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new SessionTransportSetupException(
            $"Could not connect to symmetric WDCable {channel.GetProtocolName()} channel at {host}:{port}",
            lastError ?? new IOException("Connection attempts exhausted."));
    }

    private async Task ValidateSymmetricProbeAsync(
        ISessionTransport transport,
        ProtocolChannel channel,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.SymmetricProbeTimeout);

        try
        {
            var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["kind"] = TransportProbeKind,
                ["appId"] = ProtocolConstants.AppId,
                ["protocolVersion"] = ProtocolConstants.Version,
                ["channel"] = channel.GetProtocolName()
            });
            var probe = new ProtocolFrame(
                ProtocolFrameType.ControlMessage,
                channel,
                correlationId: Guid.NewGuid(),
                metadataJson: metadata);

            await transport.WriteFrameAsync(probe, timeout.Token).ConfigureAwait(false);
            var response = await transport.ReadFrameAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new PeerProtocolMissingException("Peer closed before symmetric transport probe");

            ValidateProbeFrame(response, channel);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionTransportSetupException(
                $"Timed out waiting for symmetric {channel.GetProtocolName()} transport probe",
                ex);
        }
        catch (ProtocolException ex) when (ex.Error == ProtocolError.MalformedMagic)
        {
            throw new PeerProtocolMissingException("Peer did not speak the WDCable protocol", ex);
        }
        catch (JsonException ex)
        {
            throw new PeerProtocolMissingException("Invalid symmetric transport probe metadata", ex);
        }
    }

    private static void ValidateProbeFrame(ProtocolFrame frame, ProtocolChannel expectedChannel)
    {
        if (frame.Type != ProtocolFrameType.ControlMessage || frame.Channel != expectedChannel)
        {
            throw new PeerProtocolMissingException(
                $"Expected symmetric transport probe on {expectedChannel.GetProtocolName()}, received {frame.Type.GetProtocolName()} on {frame.Channel.GetProtocolName()}");
        }

        using var document = JsonDocument.Parse(frame.MetadataJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("kind", out var kind) ||
            kind.GetString() != TransportProbeKind)
        {
            throw new PeerProtocolMissingException("Peer did not send a symmetric transport probe");
        }

        if (!root.TryGetProperty("appId", out var appId) ||
            appId.GetString() != ProtocolConstants.AppId)
        {
            throw new PeerProtocolMissingException("Peer app id is not WDCable");
        }

        var protocolVersion = root.TryGetProperty("protocolVersion", out var version) &&
                              version.TryGetInt32(out var parsedVersion)
            ? parsedVersion
            : -1;
        if (protocolVersion != ProtocolConstants.Version)
        {
            throw new ProtocolException(
                ProtocolError.UnsupportedVersion,
                $"Peer selected unsupported protocol {protocolVersion}");
        }

        if (!root.TryGetProperty("channel", out var channel) ||
            channel.GetString() != expectedChannel.GetProtocolName())
        {
            throw new PeerProtocolMissingException("Peer sent a symmetric transport probe for the wrong channel");
        }
    }

    private async Task<TransportCandidate> SelectSymmetricCandidateAsync(
        ProtocolChannel channel,
        Task<TransportCandidate> inboundTask,
        Task<TransportCandidate> outboundTask,
        bool preferOutbound,
        CancellationToken cancellationToken)
    {
        var preferredTask = preferOutbound ? outboundTask : inboundTask;
        var alternateTask = preferOutbound ? inboundTask : outboundTask;
        Exception? firstFailure = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryGetCompletedCandidate(preferredTask, out var preferred))
            {
                _emitStatus?.Invoke($"Selected preferred {preferred.Direction.ToString().ToLowerInvariant()} {channel.GetProtocolName()} candidate");
                return preferred;
            }

            if (TryGetCompletedCandidate(alternateTask, out var alternate))
            {
                var grace = Task.Delay(_options.SymmetricPreferredCandidateGrace, cancellationToken);
                var completed = await Task.WhenAny(preferredTask, grace).ConfigureAwait(false);
                if (completed == preferredTask && TryGetCompletedCandidate(preferredTask, out preferred))
                {
                    CloseTransport(alternate.Transport);
                    _emitStatus?.Invoke($"Selected preferred {preferred.Direction.ToString().ToLowerInvariant()} {channel.GetProtocolName()} candidate");
                    return preferred;
                }

                firstFailure ??= GetTaskFailure(preferredTask);
                _emitStatus?.Invoke($"Selected alternate {alternate.Direction.ToString().ToLowerInvariant()} {channel.GetProtocolName()} candidate");
                return alternate;
            }

            if (IsFailed(preferredTask))
            {
                firstFailure ??= GetTaskFailure(preferredTask);
                if (IsFailed(alternateTask))
                {
                    throw BuildSymmetricFailure(channel, firstFailure, GetTaskFailure(alternateTask));
                }

                var candidate = await AwaitCandidateOrThrow(alternateTask, channel, firstFailure).ConfigureAwait(false);
                _emitStatus?.Invoke($"Selected alternate {candidate.Direction.ToString().ToLowerInvariant()} {channel.GetProtocolName()} candidate after preferred failed");
                return candidate;
            }

            if (IsFailed(alternateTask))
            {
                firstFailure ??= GetTaskFailure(alternateTask);
                var candidate = await AwaitCandidateOrThrow(preferredTask, channel, firstFailure).ConfigureAwait(false);
                _emitStatus?.Invoke($"Selected preferred {candidate.Direction.ToString().ToLowerInvariant()} {channel.GetProtocolName()} candidate after alternate failed");
                return candidate;
            }

            await Task.WhenAny(inboundTask, outboundTask).ConfigureAwait(false);
        }
    }

    private async Task<TransportCandidate> AwaitCandidateOrThrow(
        Task<TransportCandidate> task,
        ProtocolChannel channel,
        Exception? firstFailure)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw BuildSymmetricFailure(channel, firstFailure, ex);
        }
    }

    private Exception BuildSymmetricFailure(
        ProtocolChannel channel,
        Exception? firstFailure,
        Exception? secondFailure)
    {
        var protocolFailure = FindProtocolFailure(firstFailure) ?? FindProtocolFailure(secondFailure);
        if (protocolFailure != null)
        {
            return protocolFailure;
        }

        var firstMessage = firstFailure?.Message;
        var secondMessage = secondFailure?.Message;
        var message = string.IsNullOrWhiteSpace(firstMessage)
            ? $"Could not establish symmetric WDCable {channel.GetProtocolName()} channel"
            : string.IsNullOrWhiteSpace(secondMessage)
                ? firstMessage
                : $"{firstMessage}; {secondMessage}";
        return new SessionTransportSetupException(message);
    }

    private static Exception? FindProtocolFailure(Exception? exception)
    {
        return exception switch
        {
            null => null,
            PeerProtocolMissingException => exception,
            ProtocolException => exception,
            AggregateException aggregate => FindProtocolFailure(aggregate.InnerException),
            _ => exception.InnerException == null ? null : FindProtocolFailure(exception.InnerException)
        };
    }

    private static bool TryGetCompletedCandidate(
        Task<TransportCandidate> task,
        out TransportCandidate candidate)
    {
        if (task.IsCompletedSuccessfully)
        {
            candidate = task.Result;
            return true;
        }

        candidate = default!;
        return false;
    }

    private static bool IsFailed(Task task)
    {
        return task.IsFaulted || task.IsCanceled;
    }

    private static Exception? GetTaskFailure(Task task)
    {
        if (task.IsFaulted)
        {
            return task.Exception?.InnerException ?? task.Exception;
        }

        return task.IsCanceled ? new OperationCanceledException() : null;
    }

    private static bool ShouldRetryWithSymmetric(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is SessionTransportSetupException or OperationCanceledException;
    }

    private static bool PreferOutbound(string localAddress, string remoteAddress)
    {
        if (!IPAddress.TryParse(localAddress, out var local) ||
            !IPAddress.TryParse(remoteAddress, out var remote))
        {
            return string.CompareOrdinal(localAddress, remoteAddress) < 0;
        }

        var comparison = CompareIPAddress(local, remote);
        return comparison <= 0;
    }

    private static int CompareIPAddress(IPAddress left, IPAddress right)
    {
        var leftBytes = NormalizeAddressBytes(left);
        var rightBytes = NormalizeAddressBytes(right);
        for (var i = 0; i < leftBytes.Length && i < rightBytes.Length; i++)
        {
            var comparison = leftBytes[i].CompareTo(rightBytes[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftBytes.Length.CompareTo(rightBytes.Length);
    }

    private static byte[] NormalizeAddressBytes(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().GetAddressBytes();
        }

        return address.GetAddressBytes();
    }

    private bool IsExpectedRemote(ISessionTransport transport, string expectedRemoteAddress)
    {
        if (!IPAddress.TryParse(expectedRemoteAddress, out var expectedAddress) ||
            transport.RemoteEndPoint is not IPEndPoint remoteEndPoint)
        {
            return true;
        }

        var actualAddress = remoteEndPoint.Address;
        if (actualAddress.AddressFamily == AddressFamily.InterNetworkV6 && actualAddress.IsIPv4MappedToIPv6)
        {
            actualAddress = actualAddress.MapToIPv4();
        }

        if (expectedAddress.AddressFamily == AddressFamily.InterNetworkV6 && expectedAddress.IsIPv4MappedToIPv6)
        {
            expectedAddress = expectedAddress.MapToIPv4();
        }

        return actualAddress.Equals(expectedAddress);
    }

    private TimeSpan ConnectRetryDelay(int attempt)
    {
        var milliseconds = _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, _options.MaxRetryDelay.TotalMilliseconds));
    }

    private static IPAddress ParseAddressOrAny(string? address)
    {
        return IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Any;
    }

    private static void CloseUnselectedOnCompletion(
        Task<TransportCandidate> task,
        ISessionTransport selectedTransport)
    {
        if (task.IsCompletedSuccessfully)
        {
            if (!ReferenceEquals(task.Result.Transport, selectedTransport))
            {
                CloseTransport(task.Result.Transport);
            }

            return;
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Status == TaskStatus.RanToCompletion &&
                    !ReferenceEquals(completedTask.Result.Transport, selectedTransport))
                {
                    CloseTransport(completedTask.Result.Transport);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void CloseTransports(IEnumerable<ISessionTransport> transports)
    {
        foreach (var transport in transports)
        {
            CloseTransport(transport);
        }
    }

    private static void CloseTransport(ISessionTransport transport)
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

    private enum CandidateDirection
    {
        Inbound,
        Outbound
    }

    private sealed record TransportCandidate(
        ISessionTransport Transport,
        CandidateDirection Direction);
}
