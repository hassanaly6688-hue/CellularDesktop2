using CellularDesktop.Models;
using Windows.Devices.Enumeration;
using Windows.Devices.Sms;

namespace CellularDesktop.Services;

/// <summary>
/// Native SIM access via the WinRT Windows.Devices.Sms API (SmsDevice / SmsDevice2). This gives
/// direct access to a built-in cellular radio (e.g. Surface Pro LTE) without going through a
/// virtual COM port. IMPORTANT: SmsDevice requires the "cellularMessaging" restricted capability,
/// which in turn requires the app to run with package identity (MSIX) - see
/// Package.appxmanifest.template. An unpackaged .exe will fail to construct SmsDevice and this
/// service reports itself unavailable so the caller can fall back to AtCommandModemService.
///
/// There is no public WinRT surface for placing/answering a mobile-network voice call from a
/// third-party desktop app (Windows.ApplicationModel.Calls targets VoIP calling apps, not the
/// cellular voice line), so SupportsCalls is false here - calling still goes through the
/// AT-command path even when SMS is on the native path.
/// </summary>
public sealed class SmsDeviceModemService : IModemService
{
    private SmsDevice? _device;
    private SmsDeviceMessageStore? _store;

    public ModemStatus Status { get; private set; } = new();
    public bool SupportsCalls => false;

    public event EventHandler<ModemStatus>? StatusChanged;
    public event EventHandler<SmsMessage>? MessageReceived;
    public event EventHandler<string>? IncomingCall; // never raised - see class remarks
    public event EventHandler<CallLogEntry>? CallStateChanged; // never raised

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var selector = SmsDevice.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            return devices.Count > 0;
        }
        catch
        {
            // Throws in an unpackaged process, or when no SIM-capable radio exists.
            return false;
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var selector = SmsDevice.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            if (devices.Count == 0)
            {
                SetStatus(false, ModemTransport.None, "No native cellular radio found");
                return false;
            }

            _device = await SmsDevice.FromIdAsync(devices[0].Id);
            _store = _device.MessageStore;
            _store.MessageReceived += OnMessageReceived;

            SetStatus(true, ModemTransport.NativeWinRt, $"Connected via native modem: {devices[0].Name}");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(false, ModemTransport.None,
                $"Native SMS unavailable ({ex.Message}). Falling back to AT-command modem is recommended.");
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        if (_store is not null) _store.MessageReceived -= OnMessageReceived;
        _device = null;
        _store = null;
        SetStatus(false, ModemTransport.None, "Disconnected");
        return Task.CompletedTask;
    }

    public async Task<bool> SendSmsAsync(string phoneNumber, string body, CancellationToken ct = default)
    {
        if (_store is null) return false;
        try
        {
            var message = new SmsTextMessage
            {
                To = phoneNumber,
                Body = body
            };
            await _store.SendMessageAsync(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Calling isn't supported over this transport - see class remarks.
    public Task<bool> DialAsync(string phoneNumber, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> AnswerAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> HangUpAsync(CancellationToken ct = default) => Task.FromResult(false);

    private async void OnMessageReceived(SmsDeviceMessageStore sender, SmsMessageReceivedTriggerDetails args)
    {
        try
        {
            var smsMessage = await sender.GetMessageAsync(args.MessageIndex);
            if (smsMessage is SmsTextMessage textMessage)
            {
                MessageReceived?.Invoke(this, new SmsMessage
                {
                    PhoneNumber = textMessage.From,
                    Body = textMessage.Body,
                    Timestamp = textMessage.Timestamp,
                    Direction = SmsDirection.Incoming,
                    Status = SmsStatus.Received
                });
                await sender.DeleteMessageAsync(args.MessageIndex);
            }
        }
        catch
        {
            // Best-effort: a malformed/late trigger shouldn't crash the app.
        }
    }

    private void SetStatus(bool connected, ModemTransport transport, string info)
    {
        Status = new ModemStatus { IsConnected = connected, Transport = transport, DisplayInfo = info };
        StatusChanged?.Invoke(this, Status);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(DisconnectAsync());
    }
}
