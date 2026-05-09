namespace BeeMemoryBank.Web.Models;

public record RestoreProgressDto(
    Guid? EventId,
    string CurrentStep,
    int PercentageComplete,
    string? StatusMessage,
    string? ErrorMessage,
    bool RequiresMasterPassword
);
