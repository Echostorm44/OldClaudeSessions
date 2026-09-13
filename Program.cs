using Cascade.UI;
using Cascade.UI.Backend.Etch;
using Cascade.UI.Installer.Update;   // Updater, UpdateConfig
using OldClaudeSessions;

// ── Auto-update ─────────────────────────────────────────────────────────────
// Configure the updater so the app can offer updates from GitHub Releases. Nothing is applied
// silently: MainView checks in the background and, if a newer version exists, shows an in-app
// banner asking first. Crash detection and rollback belong to the cascade-update shim, not here:
// at this point the launch marker on disk is the one the shim just wrote for *this* launch, and
// checking it from the app rolled 1.0.2 straight back to 1.0.0 (INSTALL-002). The app's only
// part is MainView's Updater.MarkHealthy(). All best-effort: never block startup.
try
{
    string installDir = AppContext.BaseDirectory;

    string appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "1.0.0";

    Updater.Configure(
        new UpdateConfig
        {
            ManifestUrl = "https://github.com/Echostorm44/OldClaudeSessions/releases/latest/download/manifest.json",
            CheckOnStartup = true,
            CheckInterval = TimeSpan.FromHours(6),
            AutoDownload = false,  // nothing is downloaded or applied without the user asking
            Channel = "stable",
        },
        appVersion,
        "win-x64",
        installDir,
        Environment.ProcessPath ?? System.IO.Path.Combine(installDir, "OldClaudeSessions.exe"));
}
catch
{
    // Any updater wiring failure is non-fatal — the app runs regardless.
}

App.Run<MainView>(config =>
{
    config.Theme = new AppleTheme(ThemeMode.Dark);
    config.SingleInstance = true;
    config.WindowSize = new Size(1280, 760);
});
