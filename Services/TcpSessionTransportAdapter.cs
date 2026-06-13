using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

public sealed class TcpSessionTransportAdapter : ISessionTransportAdapter
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AcceptPollDelay = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly List<TcpClient> _clients = [];
    private bool _isClosed;

    public async Task<ISessionTransport> AcceptAsync(
        ProtocolChannel channel,
        IPAddress localAddress,
        int port,
        Func<bool> shouldCancel,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isClosed, this);

        var listener = new TcpListener(localAddress, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        AddListener(listener);

        try
        {
            listener.Start();
            var acceptTask = listener.AcceptTcpClientAsync();

            while (!shouldCancel())
            {
                var delayTask = Task.Delay(AcceptPollDelay, cancellationToken);
                var completedTask = await Task.WhenAny(acceptTask, delayTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (completedTask == acceptTask)
                {
                    var client = await acceptTask.ConfigureAwait(false);
                    ConfigureClient(client, channel);
                    AddClient(client);
                    return new TcpSessionTransport(channel, client, RemoveClient);
                }
            }

            throw new OperationCanceledException($"Accept cancelled for {channel.GetProtocolName()} channel.", cancellationToken);
        }
        catch
        {
            CloseQuietly(listener);
            throw;
        }
        finally
        {
            RemoveListener(listener);
            CloseQuietly(listener);
        }
    }

    public async Task<ISessionTransport> ConnectAsync(
        ProtocolChannel channel,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isClosed, this);

        var client = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            ConfigureClient(client, channel);
            AddClient(client);
            return new TcpSessionTransport(channel, client, RemoveClient);
        }
        catch
        {
            CloseQuietly(client);
            throw;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _isClosed = true;

            foreach (var listener in _listeners.ToArray())
            {
                CloseQuietly(listener);
            }

            foreach (var client in _clients.ToArray())
            {
                CloseQuietly(client);
            }

            _listeners.Clear();
            _clients.Clear();
        }
    }

    public void Cancel()
    {
        Close();
    }

    public void Dispose()
    {
        Close();
    }

    private static void ConfigureClient(TcpClient client, ProtocolChannel channel)
    {
        client.NoDelay = channel != ProtocolChannel.Bulk;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    private void AddListener(TcpListener listener)
    {
        lock (_lock)
        {
            if (_isClosed)
            {
                throw new ObjectDisposedException(nameof(TcpSessionTransportAdapter));
            }

            _listeners.Add(listener);
        }
    }

    private void RemoveListener(TcpListener listener)
    {
        lock (_lock)
        {
            _listeners.Remove(listener);
        }
    }

    private void AddClient(TcpClient client)
    {
        lock (_lock)
        {
            if (_isClosed)
            {
                CloseQuietly(client);
                throw new ObjectDisposedException(nameof(TcpSessionTransportAdapter));
            }

            _clients.Add(client);
        }
    }

    private void RemoveClient(TcpClient client)
    {
        lock (_lock)
        {
            _clients.Remove(client);
        }
    }

    private static void CloseQuietly(TcpListener listener)
    {
        try
        {
            listener.Stop();
        }
        catch
        {
        }
    }

    private static void CloseQuietly(TcpClient client)
    {
        try
        {
            client.Close();
        }
        catch
        {
        }
    }

    private sealed class TcpSessionTransport : ISessionTransport
    {
        private readonly TcpClient _client;
        private readonly Action<TcpClient> _onClosed;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _isClosed;

        public TcpSessionTransport(ProtocolChannel channel, TcpClient client, Action<TcpClient> onClosed)
        {
            Channel = channel;
            _client = client;
            _onClosed = onClosed;
        }

        public ProtocolChannel Channel { get; }

        public EndPoint? RemoteEndPoint => _client.Client.RemoteEndPoint;

        public async Task<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            var stream = _client.GetStream();
            return await ProtocolCodec.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var stream = _client.GetStream();
                await ProtocolCodec.WriteFrameAsync(frame, stream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Close()
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            _onClosed(_client);

            try
            {
                _client.Close();
            }
            catch
            {
            }
        }

        public void Cancel()
        {
            Close();
        }

        public void Dispose()
        {
            Close();
            _writeLock.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
