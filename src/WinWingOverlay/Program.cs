namespace WinWingOverlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("-d", StringComparison.OrdinalIgnoreCase)))
        {
            DiagRunner.Run();
            return;
        }

        var config = OverlayConfig.Load();
        config.Save();   // materialise the file on first run so it can be edited

        using var form = new OverlayForm(config);
        Application.Run(form);
    }
}
