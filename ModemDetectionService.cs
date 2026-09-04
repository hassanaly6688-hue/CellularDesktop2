using System.IO.Ports;
using CellularDesktop.Models;

namespace CellularDesktop.Services;

/// <summary>
/// Figures out which transport to use, in priority order:
///   1. Native WinRT (Windows.Devices.Sms) - built-in modem, best integration, SMS only.
///   2. AT-command serial - USB GSM/LTE dongle, or a Bluetooth-tethered phone that has paired
///      and exposed a modem/SPP COM port. Handles both SMS and calls.
/// Reports a clear "no compatible hardware" result if neither path finds anything, instead of
/// throwing or silently doing nothing.
/// </summary>
public sealed class ModemDetectionService
{
    public async Task<IModemService?> DetectAndConnectAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Checking for a built-in cellular radio...");
        if (await SmsDeviceModemService.IsAvailableAsync())
        {
            var native = new SmsDeviceModemService();
            if (await native.ConnectAsync(ct))
            {
                progress?.Report("Connected to native cellular modem (SMS only - AT fallback needed for calls).");
                return native;
            }
            await native.DisposeAsync();
        }

        progress?.Report("Scanning serial/COM ports for an AT-command modem...");
        var candidatePort = await FindAtCapablePortAsync(progress, ct);
        if (candidatePort is not null)
        {
            var at = new AtCommandModemService(candidatePort);
            if (await at.ConnectAsync(ct))
            {
                progress?.Report($"Connected to AT-command modem on {candidatePort}.");
                return at;
            }
            await at.DisposeAsync();
        }

        progress?.Report("No compatible cellular hardware found.");
        return null;
    }

    /// <summary>
    /// Probes every available COM port with a plain "AT" command and returns the first one that
    /// answers "OK". This is how USB GSM dongles and Bluetooth SPP/modem ports are discovered -
    /// Windows doesn't reliably distinguish "this is a modem" from "this is any other serial
    /// device" at the port-enumeration level, so a live probe is the practical approach.
    /// </summary>
    private static async Task<string?> FindAtCapablePortAsync(IProgress<string>? progress, CancellationToken ct)
    {
        foreach (var portName in SerialPort.GetPortNames().Distinct().OrderBy(p => p))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Probing {portName}...");
            if (await ProbePortAsync(portName)) return portName;
        }
        return null;
    }

    private static async Task<bool> ProbePortAsync(string portName)
    {
        try
        {
            using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 800,
                WriteTimeout = 800,
            };
            port.Open();
            port.DiscardInBuffer();
            port.Write("AT\r");
            await Task.Delay(200);
            var response = port.ReadExisting();
            port.Close();
            return response.Contains("OK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Port busy, access denied, or not a modem at all - just skip it.
            return false;
        }
    }
}
