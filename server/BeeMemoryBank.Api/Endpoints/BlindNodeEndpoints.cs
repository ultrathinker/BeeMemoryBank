using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// Every route of the blind node's own subsystems (BMB-54): status, backups, the console's server
/// side and the wipe. One entry point so the pipeline — Opus-L's file — carries a single hook line.
///
/// <para>Backups, the console and the wipe are mapped ONLY in the blind role: on a full node they
/// are not merely refused, they do not exist (404) — a full node has a vault a regular user can
/// sign in to, and "Disconnect and wipe" or a restic password has no business on its surface.
/// <c>/api/blind/status</c> exists in both roles (it names the role, and the Windows app asks a
/// node what it is) and gates its callers itself.</para>
/// </summary>
public static class BlindNodeEndpoints
{
    public static void MapBlindNodeEndpoints(this WebApplication app)
    {
        app.MapBlindStatusEndpoints();

        // The DI registration when the role wiring (BMB-52) provides one, else BMB_ROLE — the same
        // contract (master a8b3c5e3), read once at startup.
        var role = app.Services.GetService<INodeRole>() ?? new EnvironmentNodeRole();
        if (!role.IsBlind) return;

        app.MapBlindBackupEndpoints();
        app.MapBlindConsoleEndpoints();
    }
}
