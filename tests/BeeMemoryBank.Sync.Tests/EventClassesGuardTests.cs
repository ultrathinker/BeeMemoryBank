using System.Reflection;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 3.1): after activation an event type either travels sealed (it can carry a title,
/// a path, a tag or a file name) or in clear (a control event a blind host needs). <see cref="EventClasses"/> is the one
/// place that says which, the same pattern as <see cref="EventAuthorizationGuardTests"/>: a new constant of
/// <see cref="EventTypes"/> left unclassified fails here, instead of quietly travelling in clear after activation.
/// </summary>
public class EventClassesGuardTests
{
    private static IReadOnlyList<string> AllEventTypeConstants() =>
        typeof(EventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

    [Fact]
    public void EveryEventType_IsSealable_Control_OrTheSealedOuterType_ExactlyOnce()
    {
        foreach (var type in AllEventTypeConstants())
        {
            var hits = new List<string>();
            if (EventClasses.Sealable.Contains(type)) hits.Add(nameof(EventClasses.Sealable));
            if (EventClasses.Control.Contains(type)) hits.Add(nameof(EventClasses.Control));
            if (type == EventClasses.Sealed) hits.Add(nameof(EventClasses.Sealed));
            hits.Should().ContainSingle(
                $"event type '{type}' must be classified exactly once in EventClasses (Sealable / Control / Sealed); " +
                $"it is in [{string.Join(", ", hits)}]. Decide whether it can carry human-authored metadata.");
        }
    }

    [Fact]
    public void EveryClassifiedEntry_IsARealEventType()
    {
        var known = AllEventTypeConstants().ToHashSet();
        foreach (var entry in EventClasses.Sealable.Concat(EventClasses.Control).Append(EventClasses.Sealed))
            known.Should().Contain(entry, $"'{entry}' is classified in EventClasses but is not an EventTypes constant");
    }

    [Fact]
    public void TheNewTypes_AreAliasesOfTheContract_AndClassifiedForAuthorization()
    {
        EventTypes.Sealed.Should().Be("sealed");
        EventTypes.MetadataKeySet.Should().Be("metadata_key_set");
        EventTypes.MetadataActivation.Should().Be("metadata_activation");

        // spec 3.1: sealed is AnyPeer (the gate runs again on the opened view), the key record and the activation
        // are superadmin decisions.
        EventAuthorization.AnyPeer.Should().Contain(EventTypes.Sealed);
        EventAuthorization.RequiresSuperadmin(EventTypes.MetadataKeySet).Should().BeTrue();
        EventAuthorization.RequiresSuperadmin(EventTypes.MetadataActivation).Should().BeTrue();
    }

    [Fact]
    public void ClearContent_IsAProtocol1To3ContentEvent_Only()
    {
        EventClasses.IsClearContent(3, EventTypes.ArticleCreate).Should().BeTrue();
        EventClasses.IsClearContent(1, EventTypes.HardDelete).Should().BeTrue();
        EventClasses.IsClearContent(4, EventTypes.ArticleCreate).Should().BeFalse("a protocol-4 content type is malformed, not clear content");
        EventClasses.IsClearContent(3, EventTypes.WhitelistAdd).Should().BeFalse();
        EventClasses.IsClearContent(4, EventTypes.Sealed).Should().BeFalse();
    }
}
