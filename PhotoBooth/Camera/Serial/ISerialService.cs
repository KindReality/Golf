using System;
using System.Threading.Tasks;

namespace SaftApp.Serial
{
    public interface ISerialService : IDisposable
    {
        bool IsOpen { get; }
        Task<bool> OpenAsync();
        void Close();
        event EventHandler<string>? LineReceived;
    }
}
