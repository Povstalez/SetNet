using SetNet.Config;
using SetNet.Core;
using SetNet.Tests.Data;

namespace SetNet.Tests;

public class MainServer(Configuration config) : BaseServer(config)
{
    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new PlayerPeer(peerInfo);
        peer.StartReceive();
        peer.TestSend();
        
        return peer;
    }
}