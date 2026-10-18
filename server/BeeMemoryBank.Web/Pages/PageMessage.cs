namespace BeeMemoryBank.Web.Pages;

/// <summary>The message of the last action of a page, as <c>_PageMessage</c> shows it inside the card the action belongs to (BMB-303).</summary>
public sealed record PageMessage(string? Success, string? Error);
