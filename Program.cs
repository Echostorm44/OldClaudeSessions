using Cascade.UI;
using Cascade.UI.Backend.Etch;
using Cascade.UI.Installer.Update;   // Updater, UpdateConfig
using Cascade.UI.Updater.Core;       // UpdateBootstrap
using OldClaudeSessions;

// ── Auto-update ─────────────────────────────────────────────────────────────
// Configure the updater so the app can offer updates from GitHub Releases. Nothing is applied
// silently: MainView checks in the background and, if a newer version exists, shows an in-app
// banner asking first. DetectCrashAndRollback is the only automatic step — it reverts a *consented*
// update that failed to reach a healthy launch. All best-effort: never block startup.
try
{
    string installDir = AppContext.BaseDirectory;
    UpdateBootstrap.DetectCrashAndRollback(installDir);
    UpdateBootstrap.BeginLaunch(installDir);

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
