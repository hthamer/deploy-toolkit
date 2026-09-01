namespace DeployToolkit.Deployer;

/// <summary>
/// Deployer shell entry point (plan §11). Classic WinForms init — the
/// <c>ApplicationConfiguration.Initialize()</c> source generator does not
/// fire under cross-targeting builds (EnableWindowsTargeting on Linux),
/// so the equivalent calls are made explicitly.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
    }
}
