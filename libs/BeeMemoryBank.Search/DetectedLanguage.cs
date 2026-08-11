namespace BeeMemoryBank.Search;

/// <summary>
/// The coarse language classification <see cref="LanguageDetector"/> assigns to a token.
/// </summary>
public enum DetectedLanguage
{
    /// <summary>
    /// No dominant alphabet (pure digits, punctuation remnants, emoji/symbols, or a token with no
    /// clear Cyrillic/Latin majority such as evenly mixed-script garbage). Stemmers pass these
    /// through unchanged.
    /// </summary>
    Unknown = 0,

    /// <summary>Latin-alphabet-dominant token.</summary>
    English,

    /// <summary>Cyrillic-alphabet-dominant token.</summary>
    Russian,
}
