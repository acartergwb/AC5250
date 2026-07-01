namespace AC5250.Session;

public class ConnectionSettings
{
    public string HostName { get; set; } = "";
    public int Port { get; set; } = 23;
    public bool UseSsl { get; set; }

    /// <summary>Optional 5250 device name / workstation ID sent as NEW-ENVIRON DEVNAME.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Optional friendly label for the session tab. Falls back to device/host.</summary>
    public string SessionName { get; set; } = "";

    public ScreenSize ScreenSize { get; set; } = ScreenSize.Normal;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(SessionName) ? SessionName
        : string.IsNullOrEmpty(DeviceName) ? $"{HostName}:{Port}"
        : $"{DeviceName}@{HostName}";
}

public enum ScreenSize
{
    Normal,   // 24x80
    Wide,     // 27x132
}
