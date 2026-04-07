using System;
using System.Threading.Tasks;

namespace SaftApp.Serial
{
    public enum SerialStatusKind
    {
        Connecting,
        Connected,
        Disconnected,
        ReconnectAttempt,
        Reconnected,
        Error,
        DataReceived,
    }

    public sealed class SerialStatusEventArgs : EventArgs
    {
        public SerialStatusKind Kind { get; }
        public string Message { get; }
        public int? Attempt { get; }

        public SerialStatusEventArgs(SerialStatusKind kind, string message, int? attempt = null)
        {
            Kind = kind;
            Message = message;
            Attempt = attempt;
        }

        public override string ToString() => Attempt.HasValue
            ? $"[{Kind}] {Message} (attempt #{Attempt})"
            : $"[{Kind}] {Message}";
    }

    public interface ISerialService : IDisposable
    {
        bool IsOpen { get; }
        Task<bool> OpenAsync();
        void Close();
        event EventHandler<string>? LineReceived;
        event EventHandler<SerialStatusEventArgs>? StatusChanged;
    }
}
