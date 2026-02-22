using RepoLens.Shared.Models;

namespace RepoLens.Engine;

/// <summary>
/// A document stored in the search index.
/// </summary>
public class IndexDocument
{
    public required string FilePath { get; set; }
    public string? Symbol { get; set; }
    public required string Content { get; set; }
    public int Line { get; set; }
    public required string Kind { get; set; }
}

/// <summary>
/// In-memory inverted index with BM25-like scoring.
/// </summary>
public class SearchIndex
{
    private readonly List<IndexDocument> _documents = [];
    private readonly Dictionary<string, List<int>> _invertedIndex = new(StringComparer.OrdinalIgnoreCase);

    // BM25 parameters
    private const double K1 = 1.2;
    private const double B = 0.75;

    public void AddDocument(IndexDocument document)
    {
        var docIndex = _documents.Count;
        _documents.Add(document);

        // Tokenize and index
        var tokens = Tokenize(document.Content);
        if (document.Symbol != null)
        {
            tokens = tokens.Concat(Tokenize(document.Symbol)).ToList();
        }

        foreach (var token in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_invertedIndex.TryGetValue(token, out var postings))
            {
                postings = [];
                _invertedIndex[token] = postings;
            }
            postings.Add(docIndex);
        }
    }

    public List<SearchResult> Search(string query, int maxResults)
    {
        var queryTokens = Tokenize(query);
        var scores = new Dictionary<int, double>();
        var avgDocLength = _documents.Count > 0
            ? _documents.Average(d => Tokenize(d.Content).Count)
            : 1.0;

        foreach (var token in queryTokens)
        {
            if (!_invertedIndex.TryGetValue(token, out var postings))
                continue;

            // IDF component
            var idf = Math.Log((_documents.Count - postings.Count + 0.5) / (postings.Count + 0.5) + 1.0);

            foreach (var docIndex in postings)
            {
                var doc = _documents[docIndex];
                var docTokens = Tokenize(doc.Content);
                var tf = docTokens.Count(t => t.Equals(token, StringComparison.OrdinalIgnoreCase));
                var docLength = docTokens.Count;

                // BM25 score
                var score = idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * docLength / avgDocLength));

                // Boost for symbol name matches
                if (doc.Symbol != null && doc.Symbol.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score *= 2.0;
                }

                scores.TryGetValue(docIndex, out var existing);
                scores[docIndex] = existing + score;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(maxResults)
            .Select(kv =>
            {
                var doc = _documents[kv.Key];
                return new SearchResult
                {
                    FilePath = doc.FilePath,
                    Symbol = doc.Symbol,
                    Snippet = doc.Content.Length > 200 ? doc.Content[..200] + "..." : doc.Content,
                    Score = kv.Value,
                    Line = doc.Line
                };
            })
            .ToList();
    }

    private static List<string> Tokenize(string text)
    {
        // Simple tokenizer: split on non-alphanumeric, also split camelCase
        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        // Also split camelCase tokens
        var expanded = new List<string>();
        foreach (var word in words)
        {
            expanded.Add(word);
            expanded.AddRange(SplitCamelCase(word));
        }

        return expanded
            .Where(w => w.Length > 1)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static IEnumerable<string> SplitCamelCase(string word)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < word.Length; i++)
        {
            if (i > 0 && char.IsUpper(word[i]) && !char.IsUpper(word[i - 1]))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            current.Append(word[i]);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
