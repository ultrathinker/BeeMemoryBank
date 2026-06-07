namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// An Entry for per-article passphrases that must be MASKED on screen but must NOT be treated by
/// Android as a credential field — otherwise the password manager / keyboard offers to save and
/// restore it (which we never want for a per-article secret). This is the native equivalent of the
/// web "-webkit-text-security" trick: don't use a password input type, mask the display instead.
/// The actual masking + autofill suppression is applied in the Android handler mapping (MauiProgram).
/// Do NOT set IsPassword on these — that would re-enable the password input type.
/// </summary>
public class SecretEntry : Entry
{
}
