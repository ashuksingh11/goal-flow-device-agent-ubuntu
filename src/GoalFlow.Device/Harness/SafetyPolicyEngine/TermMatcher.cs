using System.Text;

namespace GoalFlow.Device.Harness;

/// <summary>
/// Decides whether a blocked term (an allergen, a diet, a medical restriction)
/// occurs in text the agent wants to act on.
///
/// <para>
/// WHY THIS EXISTS: the check used to be <c>haystack.Contains(term)</c> over the
/// raw JSON of the arguments. That misses the obvious case —
/// <c>allergens: ["peanuts"]</c> did NOT block <c>"peanut butter"</c>, because
/// the plural term is not a substring of the singular phrase. Latent so far (the
/// meal demo seeds no allergens, and no recipe contains nuts), but it stops
/// being latent as soon as a goal proposes free-text groceries for a family with
/// an allergy — which is exactly the birthday/nut-allergy use case.
/// </para>
///
/// <para>
/// Two matching modes, chosen by the shape of the term:
/// </para>
/// <list type="bullet">
/// <item><b>single word</b> → TOKEN match on a shared singular stem. "peanuts"
/// blocks "peanut butter" and "roasted peanuts". Token-level, not substring, so
/// "coconut" and "butternut squash" do NOT trip a "nuts" allergy — the classic
/// false positive of naive contains().</item>
/// <item><b>multi-word</b> ("tortilla wraps") → PHRASE match, i.e. the words must
/// appear together. Requiring the whole phrase is the conservative reading.</item>
/// </list>
/// </summary>
internal static class TermMatcher
{
    /// <summary>True if <paramref name="term"/> occurs in <paramref name="text"/>.</summary>
    internal static bool Matches(string term, string text)
    {
        var needle = Normalize(term);
        if (needle.Length == 0)
        {
            return false;
        }

        var haystack = Normalize(text);
        if (needle.Contains(' '))
        {
            // Multi-word: the phrase must appear. Padded so "wrap" cannot match
            // inside "wraps" at a word boundary we didn't intend.
            return $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal)
                || haystack.Contains(needle, StringComparison.Ordinal);
        }

        var stem = Stem(needle);
        foreach (var token in haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Stem(token) == stem)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Lowercases and reduces anything that isn't a letter or digit to a space,
    /// so JSON punctuation, hyphens and units cannot hide a term
    /// ("peanut-butter", "\"peanuts\"" and "peanuts," all tokenize the same).
    /// </summary>
    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Crude singularisation — enough to tie "peanuts"→"peanut" and
    /// "tomatoes"→"tomato". Applied to BOTH sides, so even where it over-stems
    /// ("cheese"→"chee") the two sides still agree; the only cost of a rough stem
    /// here is a nonsense-looking key, not a missed or spurious block.
    /// Deliberately not a real stemmer: no new package (Tizen-lean), and this
    /// matches a small, curated mock vocabulary.
    /// </summary>
    private static string Stem(string word)
    {
        if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal))
        {
            return word[..^2];
        }

        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            return word[..^1];
        }

        return word;
    }
}
