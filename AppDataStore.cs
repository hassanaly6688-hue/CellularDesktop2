using System.IO;
using System.Text.Json;
using CellularDesktop.Models;

namespace CellularDesktop.Services;

/// <summary>
/// Minimal local persistence (JSON files under %LocalAppData%). Swappable for SQLite later
/// without touching the ViewModels, since they only depend on the Load/Save methods below.
/// </summary>
public sealed class AppDataStore
{
    private static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CellularDesktop");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppDataStore() => Directory.CreateDirectory(RootDir);

    public List<Contact> LoadContacts() => Load<Contact>("contacts.json");
    public void SaveContacts(IEnumerable<Contact> contacts) => Save("contacts.json", contacts);

    public List<SmsMessage> LoadMessages() => Load<SmsMessage>("messages.json");
    public void SaveMessages(IEnumerable<SmsMessage> messages) => Save("messages.json", messages);

    public List<CallLogEntry> LoadCallLog() => Load<CallLogEntry>("calllog.json");
    public void SaveCallLog(IEnumerable<CallLogEntry> entries) => Save("calllog.json", entries);

    private static List<T> Load<T>(string fileName)
    {
        var path = Path.Combine(RootDir, fileName);
        if (!File.Exists(path)) return new List<T>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    private static void Save<T>(string fileName, IEnumerable<T> items)
    {
        var path = Path.Combine(RootDir, fileName);
        var json = JsonSerializer.Serialize(items.ToList(), JsonOptions);
        File.WriteAllText(path, json);
    }
}
