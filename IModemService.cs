using CellularDesktop.Models;

namespace CellularDesktop.Services;

/// <summary>
/// Common contract implemented by both the AT-command (serial) modem backend and the native
/// WinRT (Windows.Devices.Sms) backend, so the ViewModel layer doesn't need to know which
/// transport is actually driving the radio.
/// </summary>
public interface IModemService : IAsyncDisposable
{
    ModemStatus Status { get; }
    bool SupportsCalls { get; }

    event EventHandler<ModemStatus>? StatusChanged;
    event EventHandler<SmsMessage>? MessageReceived;
    event EventHandler<string>? IncomingCall;     // raised with caller number
    event EventHandler<CallLogEntry>? CallStateChanged;

    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    Task<bool> SendSmsAsync(string phoneNumber, string body, CancellationToken ct = default);

    Task<bool> DialAsync(string phoneNumber, CancellationToken ct = default);
    Task<bool> AnswerAsync(CancellationToken ct = default);
    Task<bool> HangUpAsync(CancellationToken ct = default);
}
