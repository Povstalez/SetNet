using Rpc.Shared;
using SetNet.Core;
using SetNet.Rpc;

namespace Rpc.Server;

/// <summary>Handles <see cref="RpcMethods.GetTime"/> — returns the server's UTC time. Discovered like an <c>[Op]</c> on the Rpc channel.</summary>
[RpcMethod(RpcMethods.GetTime)]
public sealed class TimeHandler : IRpcHandler<TimeRequest, TimeReply>
{
    /// <inheritdoc/>
    public Task<TimeReply> HandleAsync(BasePeer peer, TimeRequest request)
        => Task.FromResult(new TimeReply { UtcNow = DateTime.UtcNow.ToString("O") });
}

/// <summary>Handles <see cref="RpcMethods.Add"/> — adds two integers.</summary>
[RpcMethod(RpcMethods.Add)]
public sealed class AddHandler : IRpcHandler<AddRequest, AddReply>
{
    /// <inheritdoc/>
    public Task<AddReply> HandleAsync(BasePeer peer, AddRequest request)
        => Task.FromResult(new AddReply { Sum = request.A + request.B });
}
