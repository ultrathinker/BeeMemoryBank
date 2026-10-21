using System.Net.Http.Headers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The courtesy a removal pays the source (<see cref="IBlindAccountLeaveHook"/>): "the owner of this device removed this copy", as <c>POST /api/sync/leave</c> under the account's own sync
/// token (it authenticates like any round, so the hub knows which device is leaving and nobody can say it for another). The hub only notes it - its page then reads "removed by the
/// device's owner" with a Revoke button; it does not revoke anything itself, because only the owner of the network can.
///
/// <para>Through the account's own metered client, BEFORE the keys are cleared (the host calls it while the account's services are still up). The pipeline COUNTS the call and never BLOCKS
/// it (<c>exempt</c>, as the paused report is): a person who paused this copy, or whose day's traffic is spent, still takes it off with a goodbye, and it is a few hundred bytes. Best
/// effort and bounded by the host's timeout: a hub that cannot be reached, a locked key store or a 401 (the owner already revoked the device) all end it quietly, and the removal goes
/// on whatever happens here.</para>
/// </summary>
public sealed class BlindSourceLeaveHook : IBlindAccountLeaveHook
{
    public static readonly BlindSourceLeaveHook Instance = new();

    public async Task LeaveAsync(BlindAccountLeaveContext context, CancellationToken ct)
    {
        var services = context.Services;
        if (services.GetRequiredService<BlindPhoneState>().CallCode is not { } target) return;

        var identity = await services.GetRequiredService<INodeIdentityRepository>().GetAsync().ConfigureAwait(false);
        if (identity is null) return;

        using var client = services.GetRequiredService<BlindHttpClientProvider>().GetClient(target, exempt: true);
        var declared = services.GetService<ISyncProtocolPolicy>()?.Declared;
        var token = await PeerAuthenticator.AuthenticateAsync(
            services.GetRequiredService<INodeAuthSigner>(), client, target.Address, identity, target.NodeId, ct, declared).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{target.Address.TrimEnd('/')}/api/sync/leave");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        // 204 is the answer; anything else (404 from an older hub, 401 from a hub that already revoked this device) changes nothing for the removal.
    }
}

/// <summary>What a refusal of the source meant, as <see cref="BlindSourceRemoval.Judge"/> reads it.</summary>
public enum BlindRefusalOutcome
{
    /// <summary>The first refusal (or the first one after a day): remembered, nothing else happens, the account goes on asking at its normal rhythm.</summary>
    FirstNoted,

    /// <summary>A refusal that came too soon after the remembered one (the same burst of requests, not a later try): it counts for nothing and the remembered time stays.</summary>
    SameBurst,

    /// <summary>A second refusal, on a later try within a day of the first: the source has removed this device.</summary>
    Removed,
}

/// <summary>
/// What an account's controller tells its host about the source's answers to the device: a refusal (a 401 or 403 from a device that had been synced) and a contact that went through. TWO
/// refusals mean "removed by the owner" (<see cref="BlindSourceRemoval.Judge"/>): the host then marks the account <see cref="BlindAccountPhases.RemovedByOwner"/>, its card says so, and the
/// scheduler leaves it alone (no retry storm). The first one is only remembered, in the account's own state. Registered per account by the host; a composition without a host has none and
/// the round simply fails as it always did.
/// </summary>
public interface IBlindSourceRemovalSink
{
    /// <summary>The source refused this device. Safe to call again; what it meant is the answer (the caller says the matching sentence).</summary>
    BlindRefusalOutcome SourceRefusedThisDevice();

    /// <summary>The source accepted this device (a round or a job went through): a remembered first refusal is forgotten.</summary>
    void SourceAnsweredThisDevice();
}

/// <summary>The one place that says which failures mean "the source removed this device", and how many of them it takes.</summary>
public static class BlindSourceRemoval
{
    /// <summary>
    /// How long after the first refusal a second one starts to count: one normal retry interval (the 15 minutes of a sync round, the ceiling of the first-load back-off). Two refusals of one burst
    /// (a long job asks the source several times in a row, a round and a job start together) are one refusal.
    /// </summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(15);

    /// <summary>A refusal older than this is forgotten: the next one is a first one again.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>What a round or a job says after the first refusal: neutral, because a proxy or a firewall in front of a healthy source can refuse once.</summary>
    public const string FirstRefusalSentence = "The source refused this device once; asking again.";

    /// <summary>What a round or a job says when the second refusal made the source's decision plain.</summary>
    public const string RemovedSentence = "The source no longer accepts this device. The copy stays until you remove it.";

    /// <summary>True for an answer of 401 or 403: the hub's way of saying it does not know this device (any more). A redirect, a 5xx or a refused certificate says nothing of the kind.</summary>
    public static bool IsRefusal(Exception error) =>
        error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };

    /// <summary>
    /// The rule, as a pure function of the remembered time and the clock: nothing remembered, one older than <see cref="Window"/> or one in the future (the clock went back) is a first refusal
    /// (remembered from <paramref name="now"/>); one younger than <see cref="MinimumGap"/> is the same burst (the remembered time stays); anything between is the second refusal.
    /// <paramref name="remember"/> is the time to keep afterwards (null once the account is removed: a person who tries again starts from nothing).
    /// </summary>
    public static BlindRefusalOutcome Judge(DateTimeOffset? remembered, DateTimeOffset now, out DateTimeOffset? remember)
    {
        if (remembered is not { } first || first > now || now - first > Window)
        {
            remember = now;
            return BlindRefusalOutcome.FirstNoted;
        }
        if (now - first < MinimumGap)
        {
            remember = first;
            return BlindRefusalOutcome.SameBurst;
        }
        remember = null;
        return BlindRefusalOutcome.Removed;
    }

    /// <summary>True when a first refusal is remembered and still within <see cref="Window"/>: a screen then says <see cref="FirstRefusalSentence"/> (an older one is forgotten, as <see cref="Judge"/> forgets it).</summary>
    public static bool RefusalIsRecent(DateTimeOffset? remembered, DateTimeOffset now) => remembered is { } at && at <= now && now - at <= Window;

    /// <summary>The sentence of a round or a job for what the refusal meant.</summary>
    public static string Sentence(BlindRefusalOutcome outcome) => outcome == BlindRefusalOutcome.Removed ? RemovedSentence : FirstRefusalSentence;

    /// <summary>True for the sentence of a refusal: a job that returns it has written it to the log itself.</summary>
    public static bool IsRefusalSentence(string? sentence) => sentence is FirstRefusalSentence or RemovedSentence;
}
