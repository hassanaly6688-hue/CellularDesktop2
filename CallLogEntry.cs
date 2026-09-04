namespace CellularDesktop.Models;

public enum CallDirection { Incoming, Outgoing }
public enum CallResult { Answered, Missed, Rejected, Failed, InProgress }

public class CallLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PhoneNumber { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public CallDirection Direction { get; set; }
    public CallResult Result { get; set; }
    public TimeSpan Duration { get; set; }
}
