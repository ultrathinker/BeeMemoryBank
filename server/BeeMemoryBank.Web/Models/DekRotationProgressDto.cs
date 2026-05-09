namespace BeeMemoryBank.Web.Models;

public record DekRotationProgressDto(
    Guid? EventId,
    string CurrentStep,
    int PercentageComplete,
    string? StatusMessage,
    string? ErrorMessage
);
