using AC5250.Hosting;
using AC5250.UI;

namespace AC5250;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm(McpStartupOptions.Parse(args)));
    }
}
