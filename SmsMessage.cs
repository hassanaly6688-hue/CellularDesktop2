namespace CellularDesktop.Models;

public enum SmsDirection { Incoming, Outgoing }
public enum SmsStatus { Pending, Sent, Delivered, Failed, Received }

public class SmsMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PhoneNumber { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public SmsDirection Direction { get; set; }
    public SmsStatus Status { get; set; }
}
