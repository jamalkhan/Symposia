using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Symposia.NodeHost.Ledger;

/// <summary>
/// Persists one network's ledger (#163) so a restart doesn't rescan the chain:
/// <c>entries.jsonl</c> (one row per line, appended per scanned chunk) and <c>cursor.json</c>
/// (<c>scannedTo</c> and that block's hash, written to a temp file and renamed). Rows append before
/// the cursor moves, so a crash costs at most a rescan of one chunk; duplicates are dropped on load.
/// </summary>
public sealed class LedgerStore(string directory)
{
    private sealed record Row(ulong Block, long LogIndex, string TxHash, long Timestamp, ulong? Epoch, string Service,
        LedgerCategory Category, string Type, string AmountWei, string Detail);

    public sealed record Cursor(ulong ScannedTo, string BlockHash);

    private string EntriesPath => Path.Combine(directory, "entries.jsonl");

    private string CursorPath => Path.Combine(directory, "cursor.json");

    public (List<LedgerEntry> Entries, Cursor? Cursor) Load()
    {
        if (!File.Exists(CursorPath))
        {
            return ([], null);
        }

        var cursor = JsonSerializer.Deserialize<Cursor>(File.ReadAllText(CursorPath));
        var entries = new List<LedgerEntry>();
        var seen = new HashSet<(string, long)>();
        if (File.Exists(EntriesPath))
        {
            foreach (var line in File.ReadLines(EntriesPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Row? r;
                try
                {
                    r = JsonSerializer.Deserialize<Row>(line);
                }
                catch (JsonException)
                {
                    continue; // a torn last line from a crash; that chunk gets rescanned
                }

                if (r is null || r.Block > cursor!.ScannedTo || !seen.Add((r.TxHash, r.LogIndex)))
                {
                    continue; // beyond the cursor: appended but never committed
                }

                entries.Add(new LedgerEntry(r.Block, r.LogIndex, r.TxHash, r.Timestamp, r.Epoch, r.Service, r.Category, r.Type,
                    BigInteger.Parse(r.AmountWei, CultureInfo.InvariantCulture), r.Detail));
            }
        }

        return (entries, cursor);
    }

    public void Append(IReadOnlyList<LedgerEntry> entries, Cursor cursor)
    {
        Directory.CreateDirectory(directory);
        if (entries.Count > 0)
        {
            File.AppendAllLines(EntriesPath, entries.Select(e => JsonSerializer.Serialize(new Row(e.Block, e.LogIndex, e.TxHash, e.Timestamp,
                e.Epoch, e.Service, e.Category, e.Type, e.AmountWei.ToString(CultureInfo.InvariantCulture), e.Detail))));
        }

        var tmp = CursorPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cursor));
        File.Move(tmp, CursorPath, overwrite: true);
    }

    /// <summary>Rewrites the files with only <paramref name="entries"/> (after a reorg rewind).</summary>
    public void Replace(IReadOnlyList<LedgerEntry> entries, Cursor cursor)
    {
        Directory.CreateDirectory(directory);
        if (File.Exists(EntriesPath))
        {
            File.Delete(EntriesPath);
        }

        Append(entries, cursor);
    }
}
