namespace BeeMemoryBank.Search;

/// <summary>
/// Curated English + Russian stop-word lists used to strip near-ubiquitous function words from a
/// SEARCH QUERY before it is scored, so a term like "the"/"и"/"a" -- which matches most of the
/// corpus and contributes almost no ranking signal while forcing a full-postings walk -- never
/// reaches <see cref="Indexing.IndexBuilder.SearchRanked"/>. This is a deliberate, standard
/// query-time behavior change; it is NOT applied at indexing time, so the index still contains
/// these terms and nothing needs to be re-indexed.
///
/// <para>
/// <b>Where in the pipeline this matches.</b> On the SURFACE token, i.e. after
/// <see cref="TextNormalization.Normalize"/>/tokenization but BEFORE stemming. Stop words are
/// defined in their natural surface form ("the", "и"), and stemming is lossy and truncation-only
/// here (it would map "does" -&gt; "doe", "были" -&gt; a truncated stem, etc.), so matching a
/// stemmed stop-word set against stemmed query tokens would be both fragile and incomplete. Each
/// listed word is pushed through the very same <see cref="TextNormalization.Normalize"/> the
/// tokenizer applies (culture-invariant lowercasing + NFKC + diacritic/ё-folding), so the set is
/// stored in exactly the form the tokenizer emits and an ordinal lookup is an exact comparison.
/// </para>
///
/// <para>
/// <b>Sources.</b> Deliberately small and well-known: the English list is the classic Snowball
/// English stop-word list (the same core function-word set NLTK and Lucene ship); the Russian list
/// is the Snowball Russian stop-word list. Both are widely used, public-domain function-word
/// inventories -- not tuned to this corpus -- so they only ever remove genuine grammatical glue,
/// never content terms a user would actually search for.
/// </para>
/// </summary>
public static class StopWords
{
    // English -- Snowball English stop-word list (http://snowball.tartarus.org/algorithms/english/stop.txt),
    // the classic core function-word inventory. Contractions are written without the apostrophe
    // because the tokenizer splits on it (e.g. "don't" -> "don", "t"), so only the letter runs
    // that actually survive tokenization are worth listing.
    private static readonly string[] EnglishRaw =
    [
        "i", "me", "my", "myself", "we", "our", "ours", "ourselves", "you", "your", "yours",
        "yourself", "yourselves", "he", "him", "his", "himself", "she", "her", "hers", "herself",
        "it", "its", "itself", "they", "them", "their", "theirs", "themselves", "what", "which",
        "who", "whom", "this", "that", "these", "those", "am", "is", "are", "was", "were", "be",
        "been", "being", "have", "has", "had", "having", "do", "does", "did", "doing", "a", "an",
        "the", "and", "but", "if", "or", "because", "as", "until", "while", "of", "at", "by",
        "for", "with", "about", "against", "between", "into", "through", "during", "before",
        "after", "above", "below", "to", "from", "up", "down", "in", "out", "on", "off", "over",
        "under", "again", "further", "then", "once", "here", "there", "when", "where", "why",
        "how", "all", "any", "both", "each", "few", "more", "most", "other", "some", "such", "no",
        "nor", "not", "only", "own", "same", "so", "than", "too", "very", "s", "t", "can", "will",
        "just", "don", "should", "now",
    ];

    // Russian -- Snowball Russian stop-word list (http://snowball.tartarus.org/algorithms/russian/stop.txt).
    // Words spelled with "ё" are listed with "ё"; TextNormalization folds it to "е" exactly as it
    // does for indexed text, so both sides agree.
    private static readonly string[] RussianRaw =
    [
        "и", "в", "во", "не", "что", "он", "на", "я", "с", "со", "как", "а", "то", "все", "она",
        "так", "его", "но", "да", "ты", "к", "у", "же", "вы", "за", "бы", "по", "только", "ее",
        "мне", "было", "вот", "от", "меня", "еще", "нет", "о", "из", "ему", "теперь", "когда",
        "даже", "ну", "вдруг", "ли", "если", "уже", "или", "ни", "быть", "был", "него", "до",
        "вас", "нибудь", "опять", "уж", "вам", "ведь", "там", "потом", "себя", "ничего", "ей",
        "может", "они", "тут", "где", "есть", "надо", "ней", "для", "мы", "тебя", "их", "чем",
        "была", "сам", "чтоб", "без", "будто", "чего", "раз", "тоже", "себе", "под", "будет",
        "ж", "тогда", "кто", "этот", "того", "потому", "этого", "какой", "совсем", "ним", "здесь",
        "этом", "один", "почти", "мой", "тем", "чтобы", "нее", "сейчас", "были", "куда", "зачем",
        "всех", "никогда", "можно", "при", "наконец", "два", "об", "другой", "хоть", "после",
        "над", "больше", "тот", "через", "эти", "нас", "про", "всего", "них", "какая", "много",
        "разве", "три", "эту", "моя", "впрочем", "хорошо", "свою", "этой", "перед", "иногда",
        "лучше", "чуть", "том", "нельзя", "такой", "им", "более", "всегда", "конечно", "всю",
        "между",
    ];

    // Union of both locales, normalized exactly like the tokenizer normalizes indexed/query text,
    // so a lookup here is a direct comparison against a surface query token.
    private static readonly HashSet<string> Normalized = BuildNormalizedSet();

    private static HashSet<string> BuildNormalizedSet()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string word in EnglishRaw)
        {
            AddNormalized(set, word);
        }

        foreach (string word in RussianRaw)
        {
            AddNormalized(set, word);
        }

        return set;
    }

    private static void AddNormalized(HashSet<string> set, string word)
    {
        string normalized = TextNormalization.Normalize(word);
        if (normalized.Length > 0)
        {
            set.Add(normalized);
        }
    }

    /// <summary>
    /// True if <paramref name="normalizedSurfaceToken"/> -- a token already produced by the same
    /// tokenizer/normalizer the query pipeline uses, i.e. BEFORE stemming -- is a known English or
    /// Russian stop word and should be dropped from the query.
    /// </summary>
    public static bool IsStopWord(string normalizedSurfaceToken) =>
        !string.IsNullOrEmpty(normalizedSurfaceToken) && Normalized.Contains(normalizedSurfaceToken);
}
