namespace MySqlConnect;

static class Program
{
    public static Icon? AppIcon { get; private set; }

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        try { AppIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Application.Run(new MainForm());
    }
}
