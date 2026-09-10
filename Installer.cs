using Cascade.UI.Installer;

namespace OldClaudeSessions;

/// <summary>
/// The install/uninstall declaration, consumed by <c>cascade package --installer</c> to produce the
/// single-file setup exe. A per-user install (no admin): files land under LocalAppData with Start
/// Menu and Desktop shortcuts. Everything it writes is recorded in the install manifest, so the
/// bundled uninstaller removes it completely.
/// </summary>
// Must be PUBLIC: `cascade package --installer` discovers it by the public modifier and the
// generated wizard instantiates it from a separate assembly.
[Installer]
public sealed class OldClaudeSessionsInstaller : CascadeInstaller
{
    public override InstallerConfig Configure() => new()
    {
        AppId         = "6E0B2B4C-6D6E-4E58-9A64-2B7D0A5C1F33",
        AppName       = "OldClaudeSessions",
        Version       = ResolveVersion(),
        Publisher     = "Echostorm",
        Description   = "Find and resume the Claude Code sessions an unexpected reboot took from you.",
        InstallDir    = InstallDir.LocalAppData("OldClaudeSessions"),
        Output        = "OldClaudeSessions-Setup",
        RequiresAdmin = false,
        Shortcuts =
        [
            new Shortcut { Name = "Old Claude Sessions", TargetPath = "OldClaudeSessions.exe", Location = ShortcutLocation.StartMenu },
            new Shortcut { Name = "Old Claude Sessions", TargetPath = "OldClaudeSessions.exe", Location = ShortcutLocation.Desktop },
        ],
    };

    public override IReadOnlyList<InstallFile> Files =>
    [
        InstallFile.Directory("publish/*", dest: Dir.App, recursive: true),
    ];

    /// <summary>
    /// The app assembly's version as <c>major.minor.patch</c>; falls back to 1.0.0 when unset.
    /// </summary>
    private static string ResolveVersion()
    {
        Version? v = typeof(OldClaudeSessionsInstaller).Assembly.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
