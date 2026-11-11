using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace FleetBooking.Client;

public sealed class UserPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public int TextSize { get; set; } = 12;
    public bool CompactRows { get; set; }
    public bool ShowSuccessMessages { get; set; } = true;
    public bool StartMaximized { get; set; } = true;
    public bool DoubleClickReservation { get; set; } = true;
    public bool RememberTableLayouts { get; set; } = true;
    public bool RememberCalendar { get; set; } = true;
    public string StartupPage { get; set; } = "Last";
    public int LastTab { get; set; }
    public int LastAdminTab { get; set; }
    public int CalendarView { get; set; } = 1;
    public bool OnlyFreeVehicles { get; set; }
    public Dictionary<string, TablePreferences> Tables { get; set; } = [];

    public UserPreferences Copy() => JsonSerializer.Deserialize<UserPreferences>(JsonSerializer.Serialize(this))!;
    public void Normalize()
    {
        SchemaVersion = 1;
        if (TextSize is not (12 or 14 or 16)) TextSize = 12;
        if (StartupPage is not ("Last" or "Home" or "Vehicles" or "Calendar" or "Mine" or "Issues")) StartupPage = "Last";
        LastTab = Math.Clamp(LastTab, 0, 5);
        LastAdminTab = Math.Clamp(LastAdminTab, 0, 5);
        CalendarView = Math.Clamp(CalendarView, 0, 2);
        Tables ??= [];
        Tables = Tables.Where(x => x.Key.Length <= 80 && x.Value is not null).Take(12).ToDictionary(x => x.Key, x => x.Value);
        foreach (var table in Tables.Values)
        {
            table.Columns = (table.Columns ?? []).Where(c => c is not null && c.Key is { Length: > 0 and <= 160 } &&
                double.IsFinite(c.Width) && c.Width > 0 && c.Width <= 2000 &&
                c.Unit is "Pixel" or "Star" or "Auto" or "SizeToCells" or "SizeToHeader" &&
                c.DisplayIndex is >= 0 and < 32).Take(32).ToList();
            table.Sort = (table.Sort ?? []).Where(s => s is not null && s.Property is { Length: > 0 and <= 160 } &&
                Enum.IsDefined(s.Direction)).Take(8).ToList();
        }
    }
}

public sealed class TablePreferences
{
    public int LayoutVersion { get; set; } = 1;
    public List<ColumnPreference> Columns { get; set; } = [];
    public List<SortPreference> Sort { get; set; } = [];
}
public sealed record ColumnPreference(string Key, int DisplayIndex, double Width, string Unit);
public sealed record SortPreference(string Property, ListSortDirection Direction);

public sealed class UserPreferencesStore
{
    private readonly string _directory;
    public string? LastWarning { get; private set; }
    public UserPreferencesStore(string? directory = null) => _directory = directory ??
        Path.Combine(AppContext.BaseDirectory, ".local", "client", "Preferences");
    private string PathFor(Guid userId) => Path.Combine(_directory, userId.ToString("N") + ".json");
    public UserPreferences Load(Guid userId)
    {
        LastWarning = null;
        try
        {
            var path = PathFor(userId);
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException();
            var value = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            value.Normalize();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LastWarning = "Lična podešavanja nijesu mogla biti učitana. Koriste se početne vrijednosti.";
            return new();
        }
    }
    public void Save(Guid userId, UserPreferences value)
    {
        value.Normalize();
        Directory.CreateDirectory(_directory);
        var path = PathFor(userId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
