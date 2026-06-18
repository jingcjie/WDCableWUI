using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WDCableWUI.Services;

public sealed record WiFiDirectCleanupResult(
    int DiscoveredCount,
    int UnpairedCount,
    int FailedPeerCount,
    bool EnumerationFailed);

internal sealed record WiFiDirectPeerReference(string Id, string Name);

internal enum WiFiDirectPeerUnpairStatus
{
    Unpaired,
    AlreadyUnpaired,
    Failed
}

internal interface IWiFiDirectPeerStore
{
    Task<IReadOnlyList<WiFiDirectPeerReference>> GetPairedPeersAsync(
        CancellationToken cancellationToken);

    Task<WiFiDirectPeerUnpairStatus> UnpairAsync(
        WiFiDirectPeerReference peer,
        CancellationToken cancellationToken);
}

internal interface IWiFiDirectCleanupLifecycle
{
    Task StopFeatureActivityAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task StopWiFiDirectAsync(CancellationToken cancellationToken);

    Task DisposeServicesAsync(CancellationToken cancellationToken);
}

internal sealed class WiFiDirectCleanupCoordinator
{
    private readonly IWiFiDirectCleanupLifecycle _lifecycle;
    private readonly IWiFiDirectPeerStore _peerStore;
    private readonly Action<string>? _log;

    public WiFiDirectCleanupCoordinator(
        IWiFiDirectCleanupLifecycle lifecycle,
        IWiFiDirectPeerStore peerStore,
        Action<string>? log = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _peerStore = peerStore ?? throw new ArgumentNullException(nameof(peerStore));
        _log = log;
    }

    public async Task<WiFiDirectCleanupResult> CleanupAsync(
        CancellationToken cancellationToken = default)
    {
        await RunLifecycleStageAsync(
            "stop feature activity",
            _lifecycle.StopFeatureActivityAsync,
            cancellationToken).ConfigureAwait(false);
        await RunLifecycleStageAsync(
            "disconnect active session",
            _lifecycle.DisconnectAsync,
            cancellationToken).ConfigureAwait(false);
        await RunLifecycleStageAsync(
            "stop Wi-Fi Direct",
            _lifecycle.StopWiFiDirectAsync,
            cancellationToken).ConfigureAwait(false);

        var discoveredCount = 0;
        var unpairedCount = 0;
        var failedPeerCount = 0;
        var enumerationFailed = false;

        try
        {
            var peers = await _peerStore
                .GetPairedPeersAsync(cancellationToken)
                .ConfigureAwait(false);
            discoveredCount = peers.Count;

            foreach (var peer in peers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var status = await _peerStore
                        .UnpairAsync(peer, cancellationToken)
                        .ConfigureAwait(false);
                    if (status is WiFiDirectPeerUnpairStatus.Unpaired or
                        WiFiDirectPeerUnpairStatus.AlreadyUnpaired)
                    {
                        unpairedCount++;
                    }
                    else
                    {
                        failedPeerCount++;
                        _log?.Invoke($"Failed to unpair Wi-Fi Direct peer '{peer.Name}' ({peer.Id}).");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedPeerCount++;
                    _log?.Invoke(
                        $"Failed to unpair Wi-Fi Direct peer '{peer.Name}' ({peer.Id}): {ex}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            enumerationFailed = true;
            _log?.Invoke($"Failed to enumerate paired Wi-Fi Direct peers: {ex}");
        }
        finally
        {
            await RunLifecycleStageAsync(
                "dispose services",
                _lifecycle.DisposeServicesAsync,
                CancellationToken.None).ConfigureAwait(false);
        }

        return new WiFiDirectCleanupResult(
            discoveredCount,
            unpairedCount,
            failedPeerCount,
            enumerationFailed);
    }

    private async Task RunLifecycleStageAsync(
        string stage,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Wi-Fi Direct cleanup could not {stage}: {ex}");
        }
    }
}
