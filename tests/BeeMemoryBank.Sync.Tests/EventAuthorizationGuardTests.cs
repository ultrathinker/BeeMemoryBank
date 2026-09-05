using System.Reflection;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The gate in <see cref="EventApplier"/> decides, for every sync event, whether a plain
/// whitelisted peer may originate it or only a superadmin peer. It reads that decision from
/// <see cref="EventAuthorization"/>. These tests hold the two invariants that make reading from
/// one list safe:
///
/// <list type="number">
/// <item>Every <see cref="EventTypes"/> constant is classified into exactly one category. A new
/// event type added without classifying it fails the build here — instead of quietly defaulting to
/// "any whitelisted peer may send it", which is how a cluster-state event could slip past the gate.</item>
/// <item>The security-critical set — the events that require superadmin — is pinned to its exact
/// members. Moving one out (downgrading who may send it) or in is then a deliberate, reviewed
/// change with a red test, not a one-character edit.</item>
/// </list>
///
/// The behavioural half — that the applier actually refuses a superadmin-only event from a
/// non-superadmin peer and accepts a content event — lives in <see cref="JoinDefaultAuthorityTests"/>.
/// </summary>
public class EventAuthorizationGuardTests
{
    private static IReadOnlyList<string> AllEventTypeConstants() =>
        typeof(EventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

    [Fact]
    public void EveryEventType_IsClassified_InExactlyOneCategory()
    {
        var categories = new (string Name, IReadOnlySet<string> Set)[]
        {
            (nameof(EventAuthorization.SuperadminOnly), EventAuthorization.SuperadminOnly),
            (nameof(EventAuthorization.AnyPeer), EventAuthorization.AnyPeer),
            (nameof(EventAuthorization.RotationDeferredTrust), EventAuthorization.RotationDeferredTrust),
        };

        foreach (var type in AllEventTypeConstants())
        {
            var hits = categories.Where(c => c.Set.Contains(type)).Select(c => c.Name).ToList();
            hits.Should().ContainSingle(
                because: $"event type '{type}' must be classified in exactly one EventAuthorization " +
                         $"category (SuperadminOnly / AnyPeer / RotationDeferredTrust); it is in: " +
                         $"[{string.Join(", ", hits)}]. A new event type has to make a deliberate " +
                         $"choice about who may originate it — see EventAuthorization.");
        }
    }

    [Fact]
    public void Categories_ContainNoStaleOrMisspelledEntries()
    {
        var known = AllEventTypeConstants().ToHashSet();
        var classified = EventAuthorization.SuperadminOnly
            .Concat(EventAuthorization.AnyPeer)
            .Concat(EventAuthorization.RotationDeferredTrust);

        foreach (var entry in classified)
            known.Should().Contain(entry,
                because: $"'{entry}' is classified in EventAuthorization but is not a real EventTypes " +
                         $"constant — a typo or a renamed/removed event type left behind.");
    }

    [Fact]
    public void SuperadminOnly_IsExactlyTheClusterStateSet()
    {
        // Pinned on purpose. These six apply immediately and change who is trusted or what exists
        // network-wide (plus master_password_changed, which drives an admin-UI phishing surface).
        // Changing this set changes who can revoke peers, hard-delete, or trigger a restore across
        // the whole mesh from a single node — a security decision, never an incidental edit.
        EventAuthorization.SuperadminOnly.Should().BeEquivalentTo(new[]
        {
            EventTypes.WhitelistAdd,
            EventTypes.WhitelistRevoke,
            EventTypes.WhitelistUpdate,
            EventTypes.HardDelete,
            EventTypes.RestoreNetwork,
            EventTypes.MasterPasswordChanged,
        });
    }

    [Fact]
    public void RequiresSuperadmin_AgreesWithTheSuperadminOnlySet()
    {
        foreach (var type in AllEventTypeConstants())
            EventAuthorization.RequiresSuperadmin(type)
                .Should().Be(EventAuthorization.SuperadminOnly.Contains(type),
                    because: $"the applier gate for '{type}' must match its classification exactly.");
    }
}
