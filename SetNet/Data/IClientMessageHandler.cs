using System.Threading.Tasks;

namespace SetNet.Data
{
    public interface IClientMessageHandler
    {
        Task HandleAsync(byte[] data);
    }
}