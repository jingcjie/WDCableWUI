using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class WiFiDirectCleanupCoordinatorTests
{
    [TestMethod]
    public async Task NoPeersStillRunsEveryLifecycleStageInOrder()
    {
        var events = new List<string>();
        var coordinator = new WiFiDirectCleanupCoordinator(
            new FakeLifecycle(events),
            new FakePeerStore(events, []));

        var result = await coordinator.CleanupAsync();

        Assert.AreEqual(0, result.DiscoveredCount);
        Assert.AreEqual(0, result.UnpairedCount);
        Assert.AreEqual(0, result.FailedPeerCount);
        Assert.IsFalse(result.EnumerationFailed);
        CollectionAssert.AreEqual(
            new[] { "stop_features", "disconnect", "stop_wifi", "enumerate", "dispose" },
            events);
    }

    [TestMethod]
    public async Task SuccessfullyUnpairedPeersAreCounted()
    {
        var events = new List<string>();
        var peers = new[]
        {
            new WiFiDirectPeerReference("one", "One"),
            new WiFiDirectPeerReference("two", "Two")
        };
        var store = new FakePeerStore(events, peers);
        store.Results["one"] = WiFiDirectPeerUnpairStatus.Unpaired;
        store.Results["two"] = WiFiDirectPeerUnpairStatus.Unpaired;
        var coordinator = new WiFiDirectCleanupCoordinator(new FakeLifecycle(events), store);

        var result = await coordinator.CleanupAsync();

        Assert.AreEqual(2, result.DiscoveredCount);
        Assert.AreEqual(2, result.UnpairedCount);
        Assert.AreEqual(0, result.FailedPeerCount);
        Assert.IsFalse(result.EnumerationFailed);
    }

    [TestMethod]
    public async Task AlreadyUnpairedPeerCountsAsSuccessfulCleanup()
    {
        var events = new List<string>();
        var peer = new WiFiDirectPeerReference("one", "One");
        var store = new FakePeerStore(events, [peer]);
        store.Results[peer.Id] = WiFiDirectPeerUnpairStatus.AlreadyUnpaired;
        var coordinator = new WiFiDirectCleanupCoordinator(new FakeLifecycle(events), store);

        var result = await coordinator.CleanupAsync();

        Assert.AreEqual(1, result.DiscoveredCount);
        Assert.AreEqual(1, result.UnpairedCount);
        Assert.AreEqual(0, result.FailedPeerCount);
    }

    [TestMethod]
    public async Task PartialPeerFailuresDoNotStopRemainingCleanup()
    {
        var events = new List<string>();
        var peers = new[]
        {
            new WiFiDirectPeerReference("one", "One"),
            new WiFiDirectPeerReference("two", "Two"),
            new WiFiDirectPeerReference("three", "Three")
        };
        var store = new FakePeerStore(events, peers);
        store.Results["one"] = WiFiDirectPeerUnpairStatus.Unpaired;
        store.Results["two"] = WiFiDirectPeerUnpairStatus.Failed;
        store.Exceptions.Add("three");
        var coordinator = new WiFiDirectCleanupCoordinator(new FakeLifecycle(events), store);

        var result = await coordinator.CleanupAsync();

        Assert.AreEqual(3, result.DiscoveredCount);
        Assert.AreEqual(1, result.UnpairedCount);
        Assert.AreEqual(2, result.FailedPeerCount);
        Assert.IsTrue(events.Contains("unpair:three"));
        Assert.AreEqual("dispose", events[^1]);
    }

    [TestMethod]
    public async Task EnumerationFailureStillDisposesServices()
    {
        var events = new List<string>();
        var store = new FakePeerStore(events, [])
        {
            EnumerationException = new InvalidOperationException("enumeration failed")
        };
        var coordinator = new WiFiDirectCleanupCoordinator(new FakeLifecycle(events), store);

        var result = await coordinator.CleanupAsync();

        Assert.AreEqual(0, result.DiscoveredCount);
        Assert.AreEqual(0, result.UnpairedCount);
        Assert.AreEqual(0, result.FailedPeerCount);
        Assert.IsTrue(result.EnumerationFailed);
        Assert.AreEqual("dispose", events[^1]);
    }

    private sealed class FakeLifecycle(List<string> events) : IWiFiDirectCleanupLifecycle
    {
        public Task StopFeatureActivityAsync(CancellationToken cancellationToken)
        {
            events.Add("stop_features");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            events.Add("disconnect");
            return Task.CompletedTask;
        }

        public Task StopWiFiDirectAsync(CancellationToken cancellationToken)
        {
            events.Add("stop_wifi");
            return Task.CompletedTask;
        }

        public Task DisposeServicesAsync(CancellationToken cancellationToken)
        {
            events.Add("dispose");
            return Task.CompletedTask;
        }
    }

    private sealed class FakePeerStore(
        List<string> events,
        IReadOnlyList<WiFiDirectPeerReference> peers) : IWiFiDirectPeerStore
    {
        public Dictionary<string, WiFiDirectPeerUnpairStatus> Results { get; } = [];

        public HashSet<string> Exceptions { get; } = [];

        public Exception? EnumerationException { get; init; }

        public Task<IReadOnlyList<WiFiDirectPeerReference>> GetPairedPeersAsync(
            CancellationToken cancellationToken)
        {
            events.Add("enumerate");
            return EnumerationException == null
                ? Task.FromResult(peers)
                : Task.FromException<IReadOnlyList<WiFiDirectPeerReference>>(EnumerationException);
        }

        public Task<WiFiDirectPeerUnpairStatus> UnpairAsync(
            WiFiDirectPeerReference peer,
            CancellationToken cancellationToken)
        {
            events.Add($"unpair:{peer.Id}");
            if (Exceptions.Contains(peer.Id))
            {
                return Task.FromException<WiFiDirectPeerUnpairStatus>(
                    new InvalidOperationException("unpair failed"));
            }

            return Task.FromResult(
                Results.TryGetValue(peer.Id, out var result)
                    ? result
                    : WiFiDirectPeerUnpairStatus.Unpaired);
        }
    }
}
