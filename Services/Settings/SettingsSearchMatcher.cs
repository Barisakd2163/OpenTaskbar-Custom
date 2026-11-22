namespace OpenTaskbar.Services;

public sealed record SettingsSearchEntry(
    int TabIndex,
    string PageName,
    string Title,
    string Description,
    object? Target = null,
    object? Context = null);

public static class SettingsSearchMatcher
{
    public static IReadOnlyList<SettingsSearchEntry> Search(IEnumerable<SettingsSearchEntry> entries, string? query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return [];

        var terms = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];

        var results = new List<SettingsSearchEntry>();
        foreach (var entry in entries.Where(e => MatchesAllTerms(e, terms)))
        {
            results.Add(entry);
        }

        return results;
    }

    private static bool MatchesAllTerms(SettingsSearchEntry entry, string[] terms)
    {
        foreach (var term in terms)
        {
            var matched = ContainsIgnoreCase(entry.Title, term)
                || ContainsIgnoreCase(entry.Description, term)
                || ContainsIgnoreCase(entry.PageName, term);
            if (!matched) return false;
        }

        return true;
    }

    private static bool ContainsIgnoreCase(string? source, string term) =>
        !string.IsNullOrEmpty(source) && source.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}
