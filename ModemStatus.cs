namespace CellularDesktop.Models;

public enum ModemTransport { None, NativeWinRt, AtSerial }

public class ModemStatus
{
    public bool IsConnected { get; set; }
    public ModemTransport Transport { get; set; } = ModemTransport.None;
    public string DisplayInfo { get; set; } = "No cellular hardware detected";
    public string? SignalInfo { get; set; }
}
