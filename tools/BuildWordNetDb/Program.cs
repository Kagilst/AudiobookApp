using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

// Builds Assets/Dictionary/wordnet-en.db from Princeton WordNet 3.0 (English).
var repoRoot = FindRepoRoot();
var outDb = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(repoRoot, "AudiobookApp", "Assets", "Dictionary", "wordnet-en.db");
var workDir = Path.Combine(Path.GetTempPath(), "audiobookapp-wordnet-build");
Directory.CreateDirectory(workDir);

var dictDir = await EnsureWordNetDictAsync(workDir);
Console.WriteLine($"WordNet dict: {dictDir}");
Console.WriteLine($"Output DB:    {outDb}");

Directory.CreateDirectory(Path.GetDirectoryName(outDb)!);
if (File.Exists(outDb))
    File.Delete(outDb);

await using (var conn = new SqliteConnection($"Data Source={outDb}"))
{
    await conn.OpenAsync();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText =
            """
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            CREATE TABLE meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE entries (
              lemma TEXT NOT NULL COLLATE NOCASE,
              pos TEXT NOT NULL,
              sense INTEGER NOT NULL,
              definition TEXT NOT NULL,
              synonyms TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE exceptions (
              inflection TEXT NOT NULL COLLATE NOCASE,
              lemma TEXT NOT NULL COLLATE NOCASE,
              pos TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var meta = conn.CreateCommand())
    {
        meta.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v)";
        meta.Parameters.AddWithValue("$k", "source");
        meta.Parameters.AddWithValue("$v", "Princeton WordNet 3.0 (English)");
        await meta.ExecuteNonQueryAsync();
        meta.Parameters["$k"].Value = "built_utc";
        meta.Parameters["$v"].Value = DateTime.UtcNow.ToString("O");
        await meta.ExecuteNonQueryAsync();
    }

    var posFiles = new (string Pos, string DataFile, string ExcFile)[]
    {
        ("n", "data.noun", "noun.exc"),
        ("v", "data.verb", "verb.exc"),
        ("a", "data.adj", "adj.exc"),
        ("r", "data.adv", "adv.exc"),
    };

    await using var tx = conn.BeginTransaction();
    await using var insert = conn.CreateCommand();
    insert.Transaction = tx;
    insert.CommandText =
        """
        INSERT INTO entries(lemma, pos, sense, definition, synonyms)
        VALUES ($lemma, $pos, $sense, $definition, $synonyms)
        """;
    var pLemma = insert.Parameters.Add("$lemma", SqliteType.Text);
    var pPos = insert.Parameters.Add("$pos", SqliteType.Text);
    var pSense = insert.Parameters.Add("$sense", SqliteType.Integer);
    var pDef = insert.Parameters.Add("$definition", SqliteType.Text);
    var pSyn = insert.Parameters.Add("$synonyms", SqliteType.Text);

    var entryCount = 0;
    foreach (var (pos, dataFile, _) in posFiles)
    {
        var path = Path.Combine(dictDir, dataFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Missing WordNet file: {path}");

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(' '))
                continue;

            if (!TryParseDataLine(line, out var lemmas, out var definition, out var examples))
                continue;

            var gloss = examples.Count == 0
                ? definition
                : definition + "\n" + string.Join("\n", examples.Select(e => "• " + e));

            for (var i = 0; i < lemmas.Count; i++)
            {
                var lemma = lemmas[i];
                var synonyms = string.Join(", ",
                    lemmas.Where((_, idx) => idx != i).Take(8));
                pLemma.Value = lemma;
                pPos.Value = pos;
                pSense.Value = i + 1;
                pDef.Value = gloss;
                pSyn.Value = synonyms;
                await insert.ExecuteNonQueryAsync();
                entryCount++;
            }
        }

        Console.WriteLine($"  parsed {dataFile}");
    }

    await using var insertExc = conn.CreateCommand();
    insertExc.Transaction = tx;
    insertExc.CommandText =
        """
        INSERT INTO exceptions(inflection, lemma, pos)
        VALUES ($inf, $lemma, $pos)
        """;
    var eInf = insertExc.Parameters.Add("$inf", SqliteType.Text);
    var eLemma = insertExc.Parameters.Add("$lemma", SqliteType.Text);
    var ePos = insertExc.Parameters.Add("$pos", SqliteType.Text);

    var excCount = 0;
    foreach (var (pos, _, excFile) in posFiles)
    {
        var path = Path.Combine(dictDir, excFile);
        if (!File.Exists(path))
            continue;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(' '))
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var inflection = NormalizeLemma(parts[0]);
            // WordNet exception lines: inflection lemma1 lemma2...
            for (var i = 1; i < parts.Length; i++)
            {
                eInf.Value = inflection;
                eLemma.Value = NormalizeLemma(parts[i]);
                ePos.Value = pos;
                await insertExc.ExecuteNonQueryAsync();
                excCount++;
            }
        }
    }

    await tx.CommitAsync();

    await using (var idx = conn.CreateCommand())
    {
        idx.CommandText =
            """
            CREATE INDEX idx_entries_lemma ON entries(lemma);
            CREATE INDEX idx_exceptions_inflection ON exceptions(inflection);
            VACUUM;
            """;
        await idx.ExecuteNonQueryAsync();
    }

    Console.WriteLine($"Wrote {entryCount:N0} entries, {excCount:N0} exceptions.");
}

var sizeMb = new FileInfo(outDb).Length / (1024.0 * 1024.0);
Console.WriteLine($"Done. DB size: {sizeMb:0.0} MB");
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AudiobookApp.sln")))
            return dir.FullName;
        dir = dir.Parent;
    }

    // tools/BuildWordNetDb -> repo root
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}

static async Task<string> EnsureWordNetDictAsync(string workDir)
{
    var dictDir = Path.Combine(workDir, "dict");
    if (File.Exists(Path.Combine(dictDir, "data.noun")))
        return dictDir;

    var tarGz = Path.Combine(workDir, "WNdb-3.0.tar.gz");
    if (!File.Exists(tarGz))
    {
        Console.WriteLine("Downloading Princeton WordNet 3.0…");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // Official WordNet 3.0 database distribution
        var url = "https://wordnetcode.princeton.edu/3.0/WNdb-3.0.tar.gz";
        await using var remote = await http.GetStreamAsync(url);
        await using var local = File.Create(tarGz);
        await remote.CopyToAsync(local);
    }

    Console.WriteLine("Extracting…");
    await ExtractTarGzAsync(tarGz, workDir);

    if (!File.Exists(Path.Combine(dictDir, "data.noun")))
    {
        // Some archives nest as ./dict
        var found = Directory.GetFiles(workDir, "data.noun", SearchOption.AllDirectories).FirstOrDefault();
        if (found == null)
            throw new InvalidOperationException("WordNet data.noun not found after extract.");
        return Path.GetDirectoryName(found)!;
    }

    return dictDir;
}

static async Task ExtractTarGzAsync(string tarGzPath, string destDir)
{
    await using var file = File.OpenRead(tarGzPath);
    await using var gzip = new GZipStream(file, CompressionMode.Decompress);
    await TarFile.ExtractToDirectoryAsync(gzip, destDir, overwriteFiles: true);
}

static bool TryParseDataLine(
    string line,
    out List<string> lemmas,
    out string definition,
    out List<string> examples)
{
    lemmas = new List<string>();
    definition = string.Empty;
    examples = new List<string>();

    var pipe = line.IndexOf('|');
    if (pipe < 0)
        return false;

    var left = line[..pipe].Trim();
    var glossRaw = line[(pipe + 1)..].Trim();
    SplitGloss(glossRaw, out definition, out examples);
    if (string.IsNullOrWhiteSpace(definition))
        return false;

    // data line: synset_offset lex_filenum ss_type w_cnt word lex_id [word lex_id...] p_cnt ...
    var tokens = left.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (tokens.Length < 5)
        return false;

    if (!int.TryParse(tokens[3], System.Globalization.NumberStyles.HexNumber, null, out var wordCount))
        return false;

    var idx = 4;
    for (var w = 0; w < wordCount && idx + 1 < tokens.Length; w++)
    {
        var word = NormalizeLemma(tokens[idx]);
        if (!string.IsNullOrEmpty(word))
            lemmas.Add(word);
        idx += 2; // word + lex_id
    }

    return lemmas.Count > 0;
}

static void SplitGloss(string glossRaw, out string definition, out List<string> examples)
{
    examples = new List<string>();
    // Examples are quoted segments: definition; "example" ; "example2"
    var sb = new StringBuilder();
    var i = 0;
    while (i < glossRaw.Length)
    {
        if (glossRaw[i] == '"')
        {
            i++;
            var start = i;
            while (i < glossRaw.Length && glossRaw[i] != '"')
                i++;
            var example = glossRaw[start..i].Trim();
            if (example.Length > 0)
                examples.Add(example);
            if (i < glossRaw.Length)
                i++; // closing quote
            continue;
        }

        sb.Append(glossRaw[i]);
        i++;
    }

    definition = sb.ToString().Trim().TrimEnd(';').Trim();
}

static string NormalizeLemma(string raw)
{
    // WordNet uses underscores for multi-word lemmas.
    var s = raw.Trim().ToLowerInvariant().Replace('_', ' ');
    // Strip sense markers like "(a)" sometimes present — rare in data files.
    var paren = s.IndexOf('(');
    if (paren > 0)
        s = s[..paren].Trim();
    return s;
}
