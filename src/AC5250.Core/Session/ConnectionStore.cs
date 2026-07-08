using System.Text.Json;

namespace AC5250.Session;

/// <summary>
/// Persists saved connection configurations to
/// %LOCALAPPDATA%\AC5250\connections.json. Stores host/port/device/name/size only —
/// never a password (we don't hold one), so nothing sensitive is written to disk.
/// </summary>
public static class ConnectionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AC5250", "connections.json");

    public static List<ConnectionSettings> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var list = JsonSerializer.Deserialize<List<ConnectionSettings>>(File.ReadAllText(FilePath)) ?? new();

            // Backfill stable ids for connections saved before ids existed, so credentials can
            // key to them. Persist only if something actually changed.
            bool changed = false;
            foreach (var c in list)
                if (string.IsNullOrEmpty(c.Id)) { c.Id = NewId(); changed = true; }
            if (changed) Save(list);

            return list;
        }
        catch
        {
            return new();
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static void Save(List<ConnectionSettings> connections)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(connections, Options));
        }
        catch
        {
            // persistence is best-effort; a failure here must not break connecting
        }
    }

    /// <summary>Add or replace a configuration, then save. Matched by <see cref="ConnectionSettings.Id"/>
    /// when the incoming settings carries one (so a rename updates in place), otherwise by display
    /// name. A new configuration with no id is assigned one here.</summary>
    public static List<ConnectionSettings> Upsert(ConnectionSettings settings)
    {
        if (string.IsNullOrEmpty(settings.Id)) settings.Id = NewId();
        var list = Load();
        list.RemoveAll(c =>
            string.Equals(c.Id, settings.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.DisplayName, settings.DisplayName, StringComparison.OrdinalIgnoreCase));
        list.Add(settings);
        Save(list);
        return list;
    }

    public static List<ConnectionSettings> Remove(string displayName)
    {
        var list = Load();
        list.RemoveAll(c => string.Equals(c.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        Save(list);
        return list;
    }
}
