using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

public interface ISessionTransport : IAsyncDisposable, IDisposable
{
    ProtocolChannel Channel { get; }

    EndPoint? RemoteEndPoint { get; }

    Task<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken);

    Task WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken);

    void Close();

    void Cancel();
}

public interface ISessionTransportAdapter : IDisposable
{
    Task<ISessionTransport> AcceptAsync(
        ProtocolChannel channel,
        IPAddress localAddress,
        int port,
        Func<bool> shouldCancel,
        CancellationToken cancellationToken);

    Task<ISessionTransport> ConnectAsync(
        ProtocolChannel channel,
        string host,
        int port,
        CancellationToken cancellationToken);

    void Close();

    void Cancel();
}
