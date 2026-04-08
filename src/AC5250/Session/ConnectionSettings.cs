namespace AC5250.Session;

public class ConnectionSettings
{
    public string HostName { get; set; } = "";
    public int Port { get; set; } = 23;
    public bool UseSsl { get; set; }
    public string DeviceName { get; set; } = "";
    public ScreenSize ScreenSize { get; set; } = ScreenSize.Normal;

    public string DisplayName => string.IsNullOrEmpty(DeviceName)
        ? $"{HostName}:{Port}"
        : $"{DeviceName}@{HostName}";
}

public enum ScreenSize
{
    Normal,   // 24x80
    Wide,     // 27x132
}
