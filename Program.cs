namespace DynamicIsland;

/// <summary>
/// The entry point. "--hook" (Claude Code's and Codex's hooks) is answered before WPF starts, so the agent waits as
/// little as possible; everything else is the island.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
