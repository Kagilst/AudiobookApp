using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Windows.ApplicationModel;

namespace AudiobookApp.Services
{
    public sealed class DictionarySense
    {
        public string Lemma { get; init; } = string.Empty;
        public string PartOfSpeech { get; init; } = string.Empty;
        public string PartOfSpeechLabel { get; init; } = string.Empty;
        public string Definition { get; init; } = string.Empty;
        public string Synonyms { get; init; } = string.Empty;
    }

    public sealed class DictionaryLookupResult
    {
        public string Query { get; init; } = string.Empty;
        public string MatchedLemma { get; init; } = string.Empty;
        public IReadOnlyList<DictionarySense> Senses { get; init; } = Array.Empty<DictionarySense>();
        public bool Found => Senses.Count > 0;
    }

    /// <summary>
    /// Offline English dictionary backed by a Princeton WordNet 3.0 SQLite pack.
    /// </summary>
    public sealed class WordNetDictionaryService : IDisposable
    {
        public const string AssetRelativePath = @"Assets\Dictionary\wordnet-en.db";

        private static readonly Regex WordTokenRegex = new(
            @"[A-Za-z][A-Za-z'-]*",
            RegexOptions.Compiled);

        private readonly object _gate = new();
        private SqliteConnection? _connection;
        private string? _dbPath;
        private bool _initAttempted;

        public bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _connection != null;
            }
        }

        public static string ExtractLookupWord(string? selectedText)
        {
            if (string.IsNullOrWhiteSpace(selectedText))
                return string.Empty;

            var trimmed = selectedText.Trim();
            // Prefer a single selected token; otherwise first alphabetic word.
            var matches = WordTokenRegex.Matches(trimmed);
            if (matches.Count == 0)
                return string.Empty;
            if (matches.Count == 1 || !trimmed.Contains(' ', StringComparison.Ordinal))
                return matches[0].Value;

            return matches[0].Value;
        }

        public DictionaryLookupResult Lookup(string? rawWord)
        {
            EnsureInitialized();
            var query = (rawWord ?? string.Empty).Trim();
            if (_connection == null || query.Length == 0)
            {
                return new DictionaryLookupResult
                {
                    Query = query,
                    MatchedLemma = string.Empty,
                    Senses = Array.Empty<DictionarySense>()
                };
            }

            foreach (var candidate in EnumerateCandidates(query))
            {
                var senses = QueryLemma(candidate);
                if (senses.Count > 0)
                {
                    return new DictionaryLookupResult
                    {
                        Query = query,
                        MatchedLemma = candidate,
                        Senses = senses
                    };
                }
            }

            return new DictionaryLookupResult
            {
                Query = query,
                MatchedLemma = string.Empty,
                Senses = Array.Empty<DictionarySense>()
            };
        }

        public Task<DictionaryLookupResult> LookupAsync(string? rawWord, CancellationToken ct = default)
            => Task.Run(() => Lookup(rawWord), ct);

        public void Dispose()
        {
            lock (_gate)
            {
                _connection?.Dispose();
                _connection = null;
            }
        }

        private void EnsureInitialized()
        {
            if (_initAttempted)
                return;

            lock (_gate)
            {
                if (_initAttempted)
                    return;
                _initAttempted = true;

                try
                {
                    SQLitePCL.Batteries_V2.Init();
                    _dbPath = ResolveDatabasePath();
                    if (_dbPath == null || !File.Exists(_dbPath))
                        return;

                    var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
                    conn.Open();
                    _connection = conn;
                }
                catch
                {
                    _connection?.Dispose();
                    _connection = null;
                }
            }
        }

        private static string? ResolveDatabasePath()
        {
            // Unpackaged / portable: next to the executable.
            var local = Path.Combine(AppContext.BaseDirectory, "Assets", "Dictionary", "wordnet-en.db");
            if (File.Exists(local))
                return local;

            try
            {
                var installed = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "Dictionary", "wordnet-en.db");
                if (File.Exists(installed))
                    return installed;
            }
            catch
            {
                // Not running packaged.
            }

            return File.Exists(local) ? local : null;
        }

        private IEnumerable<string> EnumerateCandidates(string raw)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var form in ExpandForms(raw))
            {
                if (seen.Add(form))
                    yield return form;

                foreach (var lemma in LookupExceptions(form))
                {
                    if (seen.Add(lemma))
                        yield return lemma;
                }
            }
        }

        private static IEnumerable<string> ExpandForms(string raw)
        {
            var word = NormalizeToken(raw);
            if (word.Length == 0)
                yield break;

            yield return word;

            if (word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) && word.Length > 2)
                yield return word[..^2];
            if (word.EndsWith("s'", StringComparison.OrdinalIgnoreCase) && word.Length > 2)
                yield return word[..^2];
            if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) && word.Length > 3)
                yield return word[..^1];
            if (word.EndsWith("es", StringComparison.OrdinalIgnoreCase) && word.Length > 4)
                yield return word[..^2];
            if (word.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && word.Length > 4)
                yield return word[..^3] + "y";
            if (word.EndsWith("ed", StringComparison.OrdinalIgnoreCase) && word.Length > 4)
            {
                yield return word[..^2];
                yield return word[..^1]; // e.g. hoped -> hope
            }
            if (word.EndsWith("ing", StringComparison.OrdinalIgnoreCase) && word.Length > 5)
            {
                yield return word[..^3];
                yield return word[..^3] + "e";
            }
            if (word.EndsWith("ly", StringComparison.OrdinalIgnoreCase) && word.Length > 4)
                yield return word[..^2];
            if (word.EndsWith("er", StringComparison.OrdinalIgnoreCase) && word.Length > 4)
                yield return word[..^2];
            if (word.EndsWith("est", StringComparison.OrdinalIgnoreCase) && word.Length > 5)
                yield return word[..^3];
        }

        private static string NormalizeToken(string raw)
        {
            var s = raw.Trim().ToLowerInvariant();
            s = s.Replace('_', ' ');
            // Keep internal apostrophes; strip other punctuation.
            s = Regex.Replace(s, @"[^a-z'\s-]", string.Empty).Trim();
            return s;
        }

        private List<string> LookupExceptions(string inflection)
        {
            var list = new List<string>();
            if (_connection == null || inflection.Length == 0)
                return list;

            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT DISTINCT lemma
                    FROM exceptions
                    WHERE inflection = $w COLLATE NOCASE
                    LIMIT 8
                    """;
                cmd.Parameters.AddWithValue("$w", inflection);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(reader.GetString(0));
            }

            return list;
        }

        private IReadOnlyList<DictionarySense> QueryLemma(string lemma)
        {
            var list = new List<DictionarySense>();
            if (_connection == null)
                return list;

            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT lemma, pos, definition, synonyms
                    FROM entries
                    WHERE lemma = $w COLLATE NOCASE
                    ORDER BY
                      CASE pos WHEN 'n' THEN 0 WHEN 'v' THEN 1 WHEN 'a' THEN 2 WHEN 'r' THEN 3 ELSE 4 END,
                      rowid
                    LIMIT 40
                    """;
                cmd.Parameters.AddWithValue("$w", lemma);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var pos = reader.GetString(1);
                    list.Add(new DictionarySense
                    {
                        Lemma = reader.GetString(0),
                        PartOfSpeech = pos,
                        PartOfSpeechLabel = PosLabel(pos),
                        Definition = reader.GetString(2),
                        Synonyms = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
                    });
                }
            }

            return list;
        }

        private static string PosLabel(string pos) => pos switch
        {
            "n" => "noun",
            "v" => "verb",
            "a" => "adjective",
            "r" => "adverb",
            _ => pos
        };
    }
}
