using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CellularDesktop.Models;
using CellularDesktop.Services;

namespace CellularDesktop.ViewModels;

public sealed class MainViewModel : ObservableBase
{
    private readonly AppDataStore _store = new();
    private readonly ModemDetectionService _detector = new();
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    private IModemService? _modem;

    public ObservableCollection<Contact> Contacts { get; } = new();
    public ObservableCollection<SmsMessage> Messages { get; } = new();
    public ObservableCollection<CallLogEntry> CallLog { get; } = new();

    // Messages for whichever thread is currently selected, keyed by the other party's number.
    public ObservableCollection<SmsMessage> ActiveThread { get; } = new();

    // Distinct set of phone numbers with an SMS thread, kept in sync as messages arrive.
    public ObservableCollection<string> ThreadNumbers { get; } = new();

    private string _statusText = "Not connected";
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; set => SetField(ref _isConnected, value); }

    private bool _supportsCalls;
    public bool SupportsCalls { get => _supportsCalls; set => SetField(ref _supportsCalls, value); }

    private string _composeNumber = string.Empty;
    public string ComposeNumber { get => _composeNumber; set => SetField(ref _composeNumber, value); }

    private string _composeBody = string.Empty;
    public string ComposeBody { get => _composeBody; set => SetField(ref _composeBody, value); }

    private string _dialNumber = string.Empty;
    public string DialNumber { get => _dialNumber; set => SetField(ref _dialNumber, value); }

    private string _incomingCallBanner = string.Empty;
    public string IncomingCallBanner { get => _incomingCallBanner; set => SetField(ref _incomingCallBanner, value); }

    private bool _hasIncomingCall;
    public bool HasIncomingCall { get => _hasIncomingCall; set => SetField(ref _hasIncomingCall, value); }

    private bool _isInCall;
    public bool IsInCall { get => _isInCall; set => SetField(ref _isInCall, value); }

    private string? _selectedThreadNumber;
    public string? SelectedThreadNumber
    {
        get => _selectedThreadNumber;
        set
        {
            if (SetField(ref _selectedThreadNumber, value)) RebuildActiveThread();
        }
    }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand SendSmsCommand { get; }
    public RelayCommand DialCommand { get; }
    public RelayCommand AnswerCommand { get; }
    public RelayCommand HangUpCommand { get; }
    public RelayCommand AddContactCommand { get; }

    public MainViewModel()
    {
        ConnectCommand = new RelayCommand(async () => await ConnectAsync());
        SendSmsCommand = new RelayCommand(async () => await SendSmsAsync(), () => IsConnected && !string.IsNullOrWhiteSpace(ComposeNumber) && !string.IsNullOrWhiteSpace(ComposeBody));
        DialCommand = new RelayCommand(async () => await DialAsync(), () => IsConnected && SupportsCalls && !string.IsNullOrWhiteSpace(DialNumber) && !IsInCall);
        AnswerCommand = new RelayCommand(async () => await AnswerAsync(), () => HasIncomingCall);
        HangUpCommand = new RelayCommand(async () => await HangUpAsync(), () => IsInCall || HasIncomingCall);
        AddContactCommand = new RelayCommand(AddContact);

        foreach (var c in _store.LoadContacts()) Contacts.Add(c);
        foreach (var m in _store.LoadMessages().OrderBy(m => m.Timestamp)) Messages.Add(m);
        foreach (var e in _store.LoadCallLog().OrderByDescending(e => e.Timestamp)) CallLog.Add(e);
        foreach (var n in Messages.Select(m => m.PhoneNumber).Distinct()) ThreadNumbers.Add(n);
    }

    private void TrackThreadNumber(string phoneNumber)
    {
        if (!ThreadNumbers.Contains(phoneNumber)) ThreadNumbers.Add(phoneNumber);
    }

    private void AddContact(object? _)
    {
        var contact = new Contact { DisplayName = "New Contact", PhoneNumber = string.Empty };
        Contacts.Add(contact);
        _store.SaveContacts(Contacts);
    }

    public async Task ConnectAsync()
    {
        StatusText = "Detecting cellular hardware...";
        var progress = new Progress<string>(msg => _dispatcher.Invoke(() => StatusText = msg));

        _modem = await _detector.DetectAndConnectAsync(progress);
        if (_modem is null)
        {
            IsConnected = false;
            StatusText = "No compatible cellular hardware found. Plug in a USB GSM/LTE modem, " +
                         "pair a Bluetooth-tethered phone's modem port, or use a device with a built-in radio.";
            return;
        }

        _modem.StatusChanged += (_, status) => _dispatcher.Invoke(() => StatusText = status.DisplayInfo);
        _modem.MessageReceived += (_, sms) => _dispatcher.Invoke(() => OnMessageReceived(sms));
        _modem.IncomingCall += (_, number) => _dispatcher.Invoke(() => OnIncomingCall(number));
        _modem.CallStateChanged += (_, entry) => _dispatcher.Invoke(() => OnCallStateChanged(entry));

        IsConnected = _modem.Status.IsConnected;
        SupportsCalls = _modem.SupportsCalls;
        StatusText = _modem.Status.DisplayInfo;
    }

    private void OnMessageReceived(SmsMessage sms)
    {
        Messages.Add(sms);
        TrackThreadNumber(sms.PhoneNumber);
        _store.SaveMessages(Messages);
        if (SelectedThreadNumber == sms.PhoneNumber) ActiveThread.Add(sms);
    }

    private void OnIncomingCall(string number)
    {
        HasIncomingCall = true;
        IncomingCallBanner = $"Incoming call: {ResolveDisplayName(number)}";
        DialNumber = number;
        AnswerCommand.RaiseCanExecuteChanged();
        HangUpCommand.RaiseCanExecuteChanged();
    }

    private void OnCallStateChanged(CallLogEntry entry)
    {
        if (entry.Result == CallResult.InProgress)
        {
            IsInCall = true;
            HasIncomingCall = false;
        }
        else
        {
            IsInCall = false;
            HasIncomingCall = false;
            CallLog.Insert(0, entry);
            _store.SaveCallLog(CallLog);
        }
        IncomingCallBanner = string.Empty;
        DialCommand.RaiseCanExecuteChanged();
        AnswerCommand.RaiseCanExecuteChanged();
        HangUpCommand.RaiseCanExecuteChanged();
    }

    private async Task SendSmsAsync()
    {
        if (_modem is null) return;
        var number = ComposeNumber.Trim();
        var body = ComposeBody;

        var pending = new SmsMessage
        {
            PhoneNumber = number,
            Body = body,
            Direction = SmsDirection.Outgoing,
            Status = SmsStatus.Pending
        };
        Messages.Add(pending);
        TrackThreadNumber(number);
        if (SelectedThreadNumber == number) ActiveThread.Add(pending);

        var ok = await _modem.SendSmsAsync(number, body);
        pending.Status = ok ? SmsStatus.Sent : SmsStatus.Failed;
        _store.SaveMessages(Messages);

        if (ok) ComposeBody = string.Empty;
    }

    private async Task DialAsync()
    {
        if (_modem is null) return;
        await _modem.DialAsync(DialNumber.Trim());
    }

    private async Task AnswerAsync()
    {
        if (_modem is null) return;
        await _modem.AnswerAsync();
        IsInCall = true;
        HasIncomingCall = false;
    }

    private async Task HangUpAsync()
    {
        if (_modem is null) return;
        await _modem.HangUpAsync();
        IsInCall = false;
        HasIncomingCall = false;
    }

    public void SelectThread(string phoneNumber) => SelectedThreadNumber = phoneNumber;

    private void RebuildActiveThread()
    {
        ActiveThread.Clear();
        if (_selectedThreadNumber is null) return;
        foreach (var m in Messages.Where(m => m.PhoneNumber == _selectedThreadNumber).OrderBy(m => m.Timestamp))
            ActiveThread.Add(m);
    }

    public string ResolveDisplayName(string phoneNumber) =>
        Contacts.FirstOrDefault(c => c.PhoneNumber == phoneNumber)?.DisplayName ?? phoneNumber;
}
