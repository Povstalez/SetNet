using System.Threading.Tasks;
using SetNet.Core;

namespace SetNet.Data
{
    public interface IServerMessageHandler
    {
        Task HandleAsync(BasePeer peer, byte[] data);
    }
}