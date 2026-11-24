# Offline dictionary pack

`wordnet-en.db` is an English-only SQLite database built from **Princeton WordNet 3.0**.

Rebuild:

```powershell
dotnet run --project tools/BuildWordNetDb -c Release
```

WordNet is © 2006 Princeton University. Use of WordNet is subject to the [WordNet License](https://wordnet.princeton.edu/license-and-commercial-use).
