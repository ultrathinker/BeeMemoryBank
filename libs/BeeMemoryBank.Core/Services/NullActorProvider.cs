using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Services;

public class NullActorProvider : IActorProvider
{
    public string ActorType => "system";
    public string? ActorName => null;
    public string? ViaAgentName => null;
}
