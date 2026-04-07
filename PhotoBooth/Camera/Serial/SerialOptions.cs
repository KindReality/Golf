namespace SaftApp.Serial
{
    public class SerialOptions
    {
        public string? PortName { get; set; }
        public int BaudRate { get; set; } = 115200;
        public bool AutoOpen { get; set; } = true;
    }
}
