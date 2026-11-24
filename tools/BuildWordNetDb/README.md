# BuildWordNetDb

Downloads Princeton WordNet 3.0 and builds `AudiobookApp/Assets/Dictionary/wordnet-en.db`.

```powershell
dotnet run --project tools/BuildWordNetDb -c Release
```

Optional output path:

```powershell
dotnet run --project tools/BuildWordNetDb -c Release -- path\to\wordnet-en.db
```

WordNet is © Princeton University. Redistribution of the database is allowed under the WordNet License.
