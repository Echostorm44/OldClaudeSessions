using Cascade.UI;
using Cascade.UI.Backend.Etch;

App.Run<OldClaudeSessions.MainView>(config =>
{
    config.Theme = new AppleTheme(ThemeMode.Dark);
    config.SingleInstance = true;
    config.WindowSize = new Size(1280, 760);
});
