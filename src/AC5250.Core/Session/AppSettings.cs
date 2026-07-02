using System.Text.Json;

namespace AC5250.Session;

/// <summary>
/// Small non-sensitive app preferences, persisted to
/// %LOCALAPPDATA%\AC5250\settings.json. Nothing secret goes here (credentials live
/// in Windows Credential Manager, connections in connections.json).
/// </summary>
public class AppSettings
{
    /// <summary>Start the MCP server automatically when the app launches. Default ON so
    /// an MCP client (Claude) can connect without the user starting it each time.</summary>
    public bool StartMcpOnStartup { get; set; } = true;
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AC5250", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // preferences are best-effort; a failure here must not break the app
        }
    }
}
