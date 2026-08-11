namespace BeeMemoryBank.Search;

/// <summary>
/// Truncation-only Russian stemmer, inspired by the suffix tables used by literature Russian
/// Porter-style stemmers (e.g. "Russian stemming algorithm" / snowball's Russian rules) but
/// restricted to pure deletion — see <see cref="IStemmer"/> for why. Unlike the textbook
/// algorithms, this does not group endings into grammatical "measure" classes (adjectival vs.
/// verbal vs. noun paradigms with participle/reflexive handling) or apply them in staged passes;
/// it is a single flat table of common case/number/tense endings, tried longest-first to a fixed
/// point (see <see cref="SuffixStripper"/>). That is a deliberate simplification: the brief calls
/// for "good-enough recall over perfect precision", not full academic-grade morphology.
/// </summary>
/// <remarks>
/// <para>
/// No rule here ever substitutes a character (e.g. no vowel-reduction or consonant-alternation
/// handling, which real Russian morphology has plenty of) — only deletion, so every result is a
/// true prefix of the input by construction. This means some rules occasionally strip a real
/// root's trailing letters when they happen to coincide with a case ending (e.g. "остров"
/// (island) ends in "-ов" and stems to "остр", even though "-ов" isn't a suffix there) — an
/// accepted false positive, not a bug, per the brief's stated recall-over-precision tradeoff.
/// </para>
/// <para>
/// Grouped below by grammatical role purely for readability; <see cref="SuffixStripper"/> only
/// cares about the flat, length-sorted list.
/// </para>
/// </remarks>
public sealed class RussianStemmer : IStemmer
{
    private const int MinRemainingLength = 3;

    // Sorted longest-suffix-first (3 chars, then 2, then 1) so a longer ending is always
    // preferred over a shorter one that happens to also match the same word's tail.
    private static readonly SuffixRule[] Rules =
    [
        // Adjective endings, length 3 (genitive/dative singular, instrumental/prepositional plural).
        new SuffixRule("ого", MinRemainingLength), // большого -> больш
        new SuffixRule("его", MinRemainingLength), // синего -> син
        new SuffixRule("ому", MinRemainingLength), // большому -> больш
        new SuffixRule("ему", MinRemainingLength), // синему -> син
        new SuffixRule("ыми", MinRemainingLength), // большими -> больш
        new SuffixRule("ими", MinRemainingLength), // синими -> син

        // Verb infinitive endings, length 3.
        new SuffixRule("ать", MinRemainingLength), // читать -> чит
        new SuffixRule("ять", MinRemainingLength), // гулять -> гул
        new SuffixRule("еть", MinRemainingLength), // смотреть -> смотр
        new SuffixRule("ить", MinRemainingLength), // говорить -> говор
        new SuffixRule("уть", MinRemainingLength), // тянуть -> тян
        new SuffixRule("оть", MinRemainingLength), // колоть -> кол

        // Verb present-tense personal endings, length 3.
        new SuffixRule("ешь", MinRemainingLength), // читаешь -> чита
        new SuffixRule("ишь", MinRemainingLength), // говоришь -> говор
        new SuffixRule("ете", MinRemainingLength), // читаете -> чита
        new SuffixRule("ите", MinRemainingLength), // говорите -> говор

        // Noun instrumental plural, length 3.
        new SuffixRule("ями", MinRemainingLength), // полями -> пол
        new SuffixRule("ами", MinRemainingLength), // серверами -> сервер

        // Adjective endings, length 2 (nominative singular/plural, accusative feminine).
        new SuffixRule("ая", MinRemainingLength), // новая -> нов
        new SuffixRule("яя", MinRemainingLength), // синяя -> син
        new SuffixRule("ое", MinRemainingLength), // новое -> нов
        new SuffixRule("ее", MinRemainingLength), // синее -> син
        new SuffixRule("ые", MinRemainingLength), // новые -> нов
        new SuffixRule("ие", MinRemainingLength), // синие -> син
        new SuffixRule("ый", MinRemainingLength), // новый -> нов
        new SuffixRule("ий", MinRemainingLength), // синий -> син
        new SuffixRule("ой", MinRemainingLength), // большой -> больш
        new SuffixRule("ей", MinRemainingLength), // синей -> син
        new SuffixRule("ую", MinRemainingLength), // новую -> нов
        new SuffixRule("юю", MinRemainingLength), // синюю -> син

        // Noun case endings, length 2 (genitive/dative/prepositional/instrumental plural and
        // instrumental singular).
        new SuffixRule("ов", MinRemainingLength), // серверов -> сервер
        new SuffixRule("ев", MinRemainingLength), // музеев -> музе
        new SuffixRule("ам", MinRemainingLength), // серверам -> сервер
        new SuffixRule("ям", MinRemainingLength), // полям -> пол
        new SuffixRule("ах", MinRemainingLength), // серверах -> сервер
        new SuffixRule("ях", MinRemainingLength), // полях -> пол
        new SuffixRule("ом", MinRemainingLength), // сервером -> сервер

        // Verb endings, length 2 (present tense 1st/3rd person, 3rd person plural, past tense).
        new SuffixRule("ем", MinRemainingLength), // читаем -> чита (also noun instrumental, e.g. полем -> пол)
        new SuffixRule("им", MinRemainingLength), // говорим -> говор
        new SuffixRule("ет", MinRemainingLength), // читает -> чита
        new SuffixRule("ит", MinRemainingLength), // говорит -> говор
        new SuffixRule("ют", MinRemainingLength), // читают -> чита
        new SuffixRule("ут", MinRemainingLength), // тянут -> тян
        new SuffixRule("ят", MinRemainingLength), // говорят -> говор
        new SuffixRule("ат", MinRemainingLength), // молчат -> молч
        new SuffixRule("ла", MinRemainingLength), // читала -> чита
        new SuffixRule("ло", MinRemainingLength), // читало -> чита
        new SuffixRule("ли", MinRemainingLength), // читали -> чита
        new SuffixRule("ть", MinRemainingLength), // fallback bare infinitive marker

        // Case/number endings, length 1 (the single-vowel case family covering nominative
        // plural/genitive singular for many declensions, e.g. "сервера" -> "сервер").
        new SuffixRule("а", MinRemainingLength),
        new SuffixRule("я", MinRemainingLength),
        new SuffixRule("о", MinRemainingLength),
        new SuffixRule("е", MinRemainingLength),
        new SuffixRule("и", MinRemainingLength),
        new SuffixRule("ы", MinRemainingLength),
        new SuffixRule("у", MinRemainingLength),
        new SuffixRule("ю", MinRemainingLength),
        new SuffixRule("л", MinRemainingLength), // past tense masculine, e.g. читал -> чита
    ];

    /// <inheritdoc />
    public string Stem(string? normalizedToken)
    {
        if (string.IsNullOrEmpty(normalizedToken))
        {
            return normalizedToken ?? string.Empty;
        }

        return SuffixStripper.StripToFixedPoint(normalizedToken, Rules);
    }
}
