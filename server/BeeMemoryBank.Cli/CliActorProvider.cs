using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Cli;

public class CliActorProvider : IActorProvider
{
    public string ActorType => "cli";
    public string? ActorName => null;
    public string? ViaAgentName => null;
}
