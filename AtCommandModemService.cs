using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using CellularDesktop.Models;

namespace CellularDesktop.Services;

/// <summary>
/// Drives a modem over a virtual COM port using standard Hayes "AT" commands (3GPP TS 27.005 /
/// 27.007). This is the broadly-compatible path: it works with USB GSM/LTE dongles, and with a
/// Bluetooth-tethered Android phone once it is paired and exposes a "Standard Serial over
/// Bluetooth link" (SPP) / modem COM port. SMS is sent in text mode (AT+CMGF=1) rather than PDU
/// mode for simplicity; PDU mode is left as a documented extension point for full Unicode /
/// concatenated-SMS support.
/// </summary>
public sealed class AtCommandModemService : IModemService
{
    private SerialPort? _port;
    private readonly StringBuilder _rxBuffer = new();
    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private TaskCompletionSource<string>? _pendingReply;
    private CancellationTokenSource? _readLoopCts;
    private DateTimeOffset? _activeCallStarted;
    private string? _activeCallNumber;
    private bool _activeCallIsOutgoing;

    public string PortName { get; }
    public int BaudRate { get; }

    public ModemStatus Status { get; private set; } = new();
    public bool SupportsCalls => true;

    public event EventHandler<ModemStatus>? StatusChanged;
    public event EventHandler<SmsMessage>? MessageReceived;
    public event EventHandler<string>? IncomingCall;
    public event EventHandler<CallLogEntry>? CallStateChanged;

    public AtCommandModemService(string portName, int baudRate = 115200)
    {
        PortName = portName;
        BaudRate = baudRate;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.RequestToSend,
                NewLine = "\r\n",
                ReadTimeout = 3000,
                WriteTimeout = 3000,
                DtrEnable = true,
                RtsEnable = true,
            };
            _port.Open();
            _port.DataReceived += OnDataReceived;

            // Basic handshake / init sequence.
            await SendCommandAsync("ATZ", ct);           // reset to defaults
            await SendCommandAsync("ATE0", ct);           // echo off
            await SendCommandAsync("AT+CMGF=1", ct);      // SMS text mode
            await SendCommandAsync("AT+CNMI=2,1,0,0,0", ct); // new SMS -> unsolicited +CMTI
            await SendCommandAsync("AT+CLIP=1", ct);      // caller ID on incoming calls

            SetStatus(true, ModemTransport.AtSerial, $"Connected via {PortName} (AT command mode)");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(false, ModemTransport.None, $"Failed to open {PortName}: {ex.Message}");
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        if (_port is { IsOpen: true })
        {
            _port.DataReceived -= OnDataReceived;
            try { _port.Close(); } catch { /* best effort */ }
        }
        _port?.Dispose();
        _port = null;
        SetStatus(false, ModemTransport.None, "Disconnected");
        return Task.CompletedTask;
    }

    public async Task<bool> SendSmsAsync(string phoneNumber, string body, CancellationToken ct = default)
    {
        if (_port is not { IsOpen: true }) return false;
        await _cmdLock.WaitAsync(ct);
        try
        {
            // AT+CMGS expects the number, then a ">" prompt, then the body terminated with Ctrl+Z (0x1A).
            _pendingReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            WriteRaw($"AT+CMGS=\"{phoneNumber}\"\r");
            await WaitForPromptAsync(">", TimeSpan.FromSeconds(5), ct);
            WriteRaw(body + char.ConvertFromUtf32(0x1A));

            var reply = await WaitForFinalReplyAsync(TimeSpan.FromSeconds(15), ct);
            return reply.Contains("OK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    public async Task<bool> DialAsync(string phoneNumber, CancellationToken ct = default)
    {
        if (_port is not { IsOpen: true }) return false;
        var reply = await SendCommandAsync($"ATD{phoneNumber};", ct, timeout: TimeSpan.FromSeconds(10));
        var ok = reply.Contains("OK", StringComparison.OrdinalIgnoreCase);
        if (ok)
        {
            _activeCallNumber = phoneNumber;
            _activeCallIsOutgoing = true;
            _activeCallStarted = DateTimeOffset.Now;
            CallStateChanged?.Invoke(this, new CallLogEntry
            {
                PhoneNumber = phoneNumber,
                Direction = CallDirection.Outgoing,
                Result = CallResult.InProgress,
                Timestamp = _activeCallStarted.Value
            });
        }
        return ok;
    }

    public async Task<bool> AnswerAsync(CancellationToken ct = default)
    {
        if (_port is not { IsOpen: true }) return false;
        var reply = await SendCommandAsync("ATA", ct, timeout: TimeSpan.FromSeconds(10));
        var ok = reply.Contains("OK", StringComparison.OrdinalIgnoreCase);
        if (ok)
        {
            _activeCallIsOutgoing = false;
            _activeCallStarted = DateTimeOffset.Now;
        }
        return ok;
    }

    public async Task<bool> HangUpAsync(CancellationToken ct = default)
    {
        if (_port is not { IsOpen: true }) return false;
        var reply = await SendCommandAsync("ATH", ct, timeout: TimeSpan.FromSeconds(5));
        FinalizeActiveCall(CallResult.Answered);
        return reply.Contains("OK", StringComparison.OrdinalIgnoreCase);
    }

    // ---- low-level AT plumbing -------------------------------------------------------------

    private async Task<string> SendCommandAsync(string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        await _cmdLock.WaitAsync(ct);
        try
        {
            _pendingReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            WriteRaw(command + "\r");
            return await WaitForFinalReplyAsync(timeout ?? TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    private void WriteRaw(string data) => _port?.Write(data);

    private async Task WaitForPromptAsync(string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_rxBuffer)
            {
                if (_rxBuffer.ToString().Contains(prompt))
                {
                    _rxBuffer.Clear();
                    return;
                }
            }
            await Task.Delay(50, ct);
        }
        throw new TimeoutException($"Modem did not return prompt '{prompt}'");
    }

    private async Task<string> WaitForFinalReplyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _pendingReply!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                return await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                return string.Empty;
            }
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port is null) return;
        string chunk;
        try { chunk = _port.ReadExisting(); } catch { return; }

        string snapshot;
        lock (_rxBuffer)
        {
            _rxBuffer.Append(chunk);
            snapshot = _rxBuffer.ToString();
        }

        // Final-result-code detection completes any pending command.
        if (Regex.IsMatch(snapshot, @"(^|\r\n)(OK|ERROR|\+CMS ERROR:.*|\+CME ERROR:.*)\r\n", RegexOptions.Multiline))
        {
            lock (_rxBuffer) { _rxBuffer.Clear(); }
            _pendingReply?.TrySetResult(snapshot);
        }

        ProcessUnsolicited(snapshot);
    }

    private void ProcessUnsolicited(string data)
    {
        // Incoming call ringing.
        if (data.Contains("RING"))
        {
            var clip = Regex.Match(data, @"\+CLIP:\s*""([^""]*)""");
            var number = clip.Success ? clip.Groups[1].Value : "Unknown";
            IncomingCall?.Invoke(this, number);
        }

        // Call dropped/ended.
        if (data.Contains("NO CARRIER") || data.Contains("BUSY"))
        {
            FinalizeActiveCall(data.Contains("BUSY") ? CallResult.Rejected : CallResult.Missed);
        }

        // New SMS notification: +CMTI: "SM",<index> -> fetch it with AT+CMGR=<index>.
        var cmti = Regex.Match(data, @"\+CMTI:\s*""[^""]*"",(\d+)");
        if (cmti.Success)
        {
            var index = cmti.Groups[1].Value;
            _ = FetchAndRaiseIncomingSmsAsync(index);
        }
    }

    private async Task FetchAndRaiseIncomingSmsAsync(string index)
    {
        try
        {
            var reply = await SendCommandAsync($"AT+CMGR={index}", CancellationToken.None);
            // Typical reply: +CMGR: "REC UNREAD","+15551234567",,"26/08/31,12:00:00-32"\r\n<body>\r\n\r\nOK
            var header = Regex.Match(reply, @"\+CMGR:\s*""[^""]*"",""([^""]*)"",[^,]*,""([^""]*)""");
            var bodyMatch = Regex.Match(reply, @"\+CMGR:.*\r\n(.*)\r\n\r\nOK", RegexOptions.Singleline);
            if (!header.Success) return;

            var number = header.Groups[1].Value;
            var body = bodyMatch.Success ? bodyMatch.Groups[1].Value.Trim() : string.Empty;
            var timestamp = ParseModemTimestamp(header.Groups[2].Value) ?? DateTimeOffset.Now;

            MessageReceived?.Invoke(this, new SmsMessage
            {
                PhoneNumber = number,
                Body = body,
                Timestamp = timestamp,
                Direction = SmsDirection.Incoming,
                Status = SmsStatus.Received
            });

            // Free SIM/ME storage now that it's been read into the app.
            await SendCommandAsync($"AT+CMGD={index}", CancellationToken.None);
        }
        catch
        {
            // Non-fatal: a malformed unsolicited notification shouldn't crash the read loop.
        }
    }

    private static DateTimeOffset? ParseModemTimestamp(string raw)
    {
        // Format: yy/MM/dd,HH:mm:sszz  e.g. 26/08/31,12:00:00-32 (quarter-hour UTC offset)
        var m = Regex.Match(raw, @"(\d{2})/(\d{2})/(\d{2}),(\d{2}):(\d{2}):(\d{2})([+-]\d+)");
        if (!m.Success) return null;
        try
        {
            var year = 2000 + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var offsetQuarters = int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture);
            var offset = TimeSpan.FromMinutes(offsetQuarters * 15);
            return new DateTimeOffset(year, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), offset);
        }
        catch { return null; }
    }

    private void FinalizeActiveCall(CallResult result)
    {
        if (_activeCallNumber is null || _activeCallStarted is null) return;
        var duration = DateTimeOffset.Now - _activeCallStarted.Value;
        CallStateChanged?.Invoke(this, new CallLogEntry
        {
            PhoneNumber = _activeCallNumber,
            Direction = _activeCallIsOutgoing ? CallDirection.Outgoing : CallDirection.Incoming,
            Result = result,
            Timestamp = _activeCallStarted.Value,
            Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration
        });
        _activeCallNumber = null;
        _activeCallStarted = null;
    }

    private void SetStatus(bool connected, ModemTransport transport, string info)
    {
        Status = new ModemStatus { IsConnected = connected, Transport = transport, DisplayInfo = info };
        StatusChanged?.Invoke(this, Status);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cmdLock.Dispose();
    }
}
