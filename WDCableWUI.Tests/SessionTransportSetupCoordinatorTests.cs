using System.Net;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Protocol;
using WDCableWUI.Services;
using ChannelFactory = System.Threading.Channels.Channel;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class SessionTransportSetupCoordinatorTests
{
    private const string AddressA = "192.168.49.1";
    private const string AddressB = "192.168.49.2";

    private static readonly IReadOnlyList<(ProtocolChannel Channel, int Port)> BaseChannels =
    [
        (ProtocolChannel.Control, ProtocolConstants.DefaultControlPort),
        (ProtocolChannel.Bulk, ProtocolConstants.DefaultBulkPort)
    ];

    private static readonly IReadOnlyList<(ProtocolChannel Channel, int Port)> SingleControlChannel =
    [
        (ProtocolChannel.Control, ProtocolConstants.DefaultControlPort)
    ];

    [TestMethod]
    public async Task LegacyGroupOwnerClientPathOpensAllChannels()
    {
        var network = new FakeTransportNetwork();
        var owner = CreateCoordinator(network, AddressA);
        var client = CreateCoordinator(network, AddressB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ownerTask = owner.OpenLegacyAsync(
            SessionRole.GroupOwner,
            AddressA,
            AddressB,
            BaseChannels,
            () => false,
            timeout.Token);
        var clientTask = client.OpenLegacyAsync(
            SessionRole.Client,
            AddressB,
            AddressA,
            BaseChannels,
            () => false,
            timeout.Token);

        await Task.WhenAll(ownerTask, clientTask);

        Assert.AreEqual(2, ownerTask.Result.Count);
        Assert.AreEqual(2, clientTask.Result.Count);
        CloseAll(ownerTask.Result.Values);
        CloseAll(clientTask.Result.Values);
    }

    [TestMethod]
    public async Task LegacyFailureRetriesSameLinkWithSymmetricSetup()
    {
        var network = new FakeTransportNetwork();
        var first = CreateCoordinator(network, AddressA);
        var second = CreateCoordinator(network, AddressB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var firstTask = first.OpenWithFallbackAsync(
            SessionRole.GroupOwner,
            AddressA,
            AddressB,
            BaseChannels,
            () => false,
            timeout.Token);
        var secondTask = second.OpenWithFallbackAsync(
            SessionRole.GroupOwner,
            AddressB,
            AddressA,
            BaseChannels,
            () => false,
            timeout.Token);

        await Task.WhenAll(firstTask, secondTask);

        Assert.AreEqual(2, firstTask.Result.Count);
        Assert.AreEqual(2, secondTask.Result.Count);
        CloseAll(firstTask.Result.Values);
        CloseAll(secondTask.Result.Values);
    }

    [TestMethod]
    public async Task BothClientsFallbackToSymmetricSetup()
    {
        var network = new FakeTransportNetwork();
        var first = CreateCoordinator(network, AddressA);
        var second = CreateCoordinator(network, AddressB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var firstTask = first.OpenWithFallbackAsync(
            SessionRole.Client,
            AddressA,
            AddressB,
            BaseChannels,
            () => false,
            timeout.Token);
        var secondTask = second.OpenWithFallbackAsync(
            SessionRole.Client,
            AddressB,
            AddressA,
            BaseChannels,
            () => false,
            timeout.Token);

        await Task.WhenAll(firstTask, secondTask);

        Assert.AreEqual(2, firstTask.Result.Count);
        Assert.AreEqual(2, secondTask.Result.Count);
        CloseAll(firstTask.Result.Values);
        CloseAll(secondTask.Result.Values);
    }

    [TestMethod]
    public async Task SymmetricSetupRejectsUnexpectedInboundCandidate()
    {
        var network = new FakeTransportNetwork();
        var first = CreateCoordinator(network, AddressA);
        var second = CreateCoordinator(network, AddressB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var firstTask = first.OpenSymmetricAsync(
            AddressA,
            AddressB,
            SingleControlChannel,
            () => false,
            timeout.Token);

        await network.WaitForListenerAsync(AddressA, ProtocolConstants.DefaultControlPort, timeout.Token);
        var staleCandidate = network.InjectInbound(
            ProtocolChannel.Control,
            AddressA,
            ProtocolConstants.DefaultControlPort,
            "10.0.0.77");

        var secondTask = second.OpenSymmetricAsync(
            AddressB,
            AddressA,
            SingleControlChannel,
            () => false,
            timeout.Token);

        await Task.WhenAll(firstTask, secondTask);

        Assert.IsTrue(staleCandidate.IsClosed);
        Assert.AreEqual(1, firstTask.Result.Count);
        Assert.AreEqual(1, secondTask.Result.Count);
        CloseAll(firstTask.Result.Values);
        CloseAll(secondTask.Result.Values);
    }

    [TestMethod]
    public async Task ExhaustedConnectAttemptsAreTransportSetupFailures()
    {
        var network = new FakeTransportNetwork();
        var coordinator = CreateCoordinator(network, AddressA);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsExceptionAsync<SessionTransportSetupException>(() =>
            coordinator.OpenLegacyAsync(
                SessionRole.Client,
                AddressA,
                AddressB,
                SingleControlChannel,
                () => false,
                timeout.Token));
        var args = new SessionFailedEventArgs(
            "transport_setup_failed",
            exception.Message,
            "session",
            SessionRole.Client,
            isPeerProtocolMissing: false,
            SessionFailureKind.TransportSetup);

        Assert.IsFalse(args.IsPeerProtocolMissing);
        Assert.AreEqual(SessionFailureKind.TransportSetup, args.FailureKind);
    }

    private static SessionTransportSetupCoordinator CreateCoordinator(
        FakeTransportNetwork network,
        string localAddress)
    {
        return new SessionTransportSetupCoordinator(
            network.CreateAdapter(localAddress),
            options: new SessionTransportSetupOptions
            {
                MaxConnectAttempts = 3,
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaxRetryDelay = TimeSpan.FromMilliseconds(10),
                LegacyAttemptTimeout = TimeSpan.FromMilliseconds(120),
                FallbackBackoff = TimeSpan.FromMilliseconds(10),
                SymmetricProbeTimeout = TimeSpan.FromMilliseconds(500),
                SymmetricPreferredCandidateGrace = TimeSpan.FromMilliseconds(20)
            });
    }

    private static void CloseAll(IEnumerable<ISessionTransport> transports)
    {
        foreach (var transport in transports)
        {
            transport.Cancel();
            transport.Dispose();
        }
    }

    private sealed class FakeTransportNetwork
    {
        private readonly object _lock = new();
        private readonly Dictionary<(string Address, int Port), FakeSessionTransportListener> _listeners = [];

        public FakeSessionTransportAdapter CreateAdapter(string localAddress)
        {
            return new FakeSessionTransportAdapter(this, localAddress);
        }

        public FakeSessionTransportListener Register(
            ProtocolChannel channel,
            string localAddress,
            int port)
        {
            lock (_lock)
            {
                var key = (localAddress, port);
                if (_listeners.ContainsKey(key))
                {
                    throw new IOException($"Listener already exists for {localAddress}:{port}");
                }

                var listener = new FakeSessionTransportListener(this, channel, localAddress, port);
                _listeners[key] = listener;
                Monitor.PulseAll(_lock);
                return listener;
            }
        }

        public void Unregister(string localAddress, int port, FakeSessionTransportListener listener)
        {
            lock (_lock)
            {
                var key = (localAddress, port);
                if (_listeners.TryGetValue(key, out var current) && ReferenceEquals(current, listener))
                {
                    _listeners.Remove(key);
                    Monitor.PulseAll(_lock);
                }
            }
        }

        public Task<ISessionTransport> ConnectAsync(
            ProtocolChannel channel,
            string sourceAddress,
            string remoteAddress,
            int port,
            CancellationToken cancellationToken)
        {
            FakeSessionTransportListener listener;
            lock (_lock)
            {
                if (!_listeners.TryGetValue((remoteAddress, port), out listener!))
                {
                    return Task.FromException<ISessionTransport>(
                        new IOException($"No listener for {remoteAddress}:{port}"));
                }
            }

            var pair = InMemorySessionTransport.CreatePair(channel, sourceAddress, remoteAddress);
            listener.Queue(pair.Server);
            return Task.FromResult<ISessionTransport>(pair.Client);
        }

        public InMemorySessionTransport InjectInbound(
            ProtocolChannel channel,
            string listenerAddress,
            int port,
            string remoteAddress)
        {
            FakeSessionTransportListener listener;
            lock (_lock)
            {
                listener = _listeners[(listenerAddress, port)];
            }

            var pair = InMemorySessionTransport.CreatePair(channel, remoteAddress, listenerAddress);
            listener.Queue(pair.Server);
            return pair.Server;
        }

        public Task WaitForListenerAsync(
            string localAddress,
            int port,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_lock)
                {
                    while (!_listeners.ContainsKey((localAddress, port)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Monitor.Wait(_lock, TimeSpan.FromMilliseconds(10));
                    }
                }
            }, cancellationToken);
        }
    }

    private sealed class FakeSessionTransportAdapter(
        FakeTransportNetwork network,
        string localAddress) : ISessionTransportAdapter
    {
        private readonly List<FakeSessionTransportListener> _listeners = [];
        private bool _isClosed;

        public ISessionTransportListener Listen(
            ProtocolChannel channel,
            IPAddress localAddress,
            int port)
        {
            ObjectDisposedException.ThrowIf(_isClosed, this);

            var listener = network.Register(channel, localAddress.ToString(), port);
            _listeners.Add(listener);
            return listener;
        }

        public async Task<ISessionTransport> AcceptAsync(
            ProtocolChannel channel,
            IPAddress localAddress,
            int port,
            Func<bool> shouldCancel,
            CancellationToken cancellationToken)
        {
            using var listener = Listen(channel, localAddress, port);
            return await listener.AcceptAsync(shouldCancel, cancellationToken).ConfigureAwait(false);
        }

        public Task<ISessionTransport> ConnectAsync(
            ProtocolChannel channel,
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_isClosed, this);
            return network.ConnectAsync(channel, localAddress, host, port, cancellationToken);
        }

        public void Close()
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            foreach (var listener in _listeners.ToArray())
            {
                listener.Close();
            }

            _listeners.Clear();
        }

        public void Cancel()
        {
            Close();
        }

        public void Dispose()
        {
            Close();
        }
    }

    private sealed class FakeSessionTransportListener(
        FakeTransportNetwork network,
        ProtocolChannel channel,
        string localAddress,
        int port) : ISessionTransportListener
    {
        private readonly Channel<InMemorySessionTransport> _accepted = ChannelFactory.CreateUnbounded<InMemorySessionTransport>();
        private bool _isClosed;

        public ProtocolChannel Channel => channel;

        public int Port => port;

        public async Task<ISessionTransport> AcceptAsync(
            Func<bool> shouldCancel,
            CancellationToken cancellationToken)
        {
            while (!shouldCancel())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException ex)
                {
                    throw new OperationCanceledException("Listener closed.", ex, cancellationToken);
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }

        public void Queue(InMemorySessionTransport transport)
        {
            if (_isClosed)
            {
                transport.Cancel();
                return;
            }

            _accepted.Writer.TryWrite(transport);
        }

        public void Close()
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            _accepted.Writer.TryComplete();
            network.Unregister(localAddress, port, this);
        }

        public void Dispose()
        {
            Close();
        }
    }

    private sealed class InMemorySessionTransport : ISessionTransport
    {
        private readonly Channel<ProtocolFrame> _incoming;
        private readonly Channel<ProtocolFrame> _outgoing;

        private InMemorySessionTransport(
            ProtocolChannel channel,
            EndPoint remoteEndPoint,
            Channel<ProtocolFrame> incoming,
            Channel<ProtocolFrame> outgoing)
        {
            Channel = channel;
            RemoteEndPoint = remoteEndPoint;
            _incoming = incoming;
            _outgoing = outgoing;
        }

        public ProtocolChannel Channel { get; }

        public EndPoint? RemoteEndPoint { get; }

        public bool IsClosed { get; private set; }

        public static (InMemorySessionTransport Client, InMemorySessionTransport Server) CreatePair(
            ProtocolChannel channel,
            string clientAddress,
            string serverAddress)
        {
            var clientIncoming = ChannelFactory.CreateUnbounded<ProtocolFrame>();
            var serverIncoming = ChannelFactory.CreateUnbounded<ProtocolFrame>();
            var client = new InMemorySessionTransport(
                channel,
                new IPEndPoint(IPAddress.Parse(serverAddress), 50000),
                clientIncoming,
                serverIncoming);
            var server = new InMemorySessionTransport(
                channel,
                new IPEndPoint(IPAddress.Parse(clientAddress), 50000),
                serverIncoming,
                clientIncoming);
            return (client, server);
        }

        public async Task<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public async Task WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken)
        {
            await _outgoing.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        public void Close()
        {
            if (IsClosed)
            {
                return;
            }

            IsClosed = true;
            _outgoing.Writer.TryComplete();
        }

        public void Cancel()
        {
            Close();
        }

        public void Dispose()
        {
            Close();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
