using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using POSscan.Core;

namespace POSscan.Data;

public enum ReceiptStatus { Draft, Exported, Imported, Voided }

public sealed record AppSettings
{
    public string StoreName { get; init; } = "THE SQUIRREL COVE GENERAL STORE";
    public string StoreNumber { get; init; } = "815";
    public string OutputFolder { get; init; } = "";
    public string ScanFolder { get; init; } = "";
    public decimal PriceChangePercent { get; init; } = 25m;
    public bool ExportWeighed { get; init; }
    /// <summary>True keeps the report text around the rows, as in the known RAS CSV.</summary>
    public bool FullRasLayout { get; init; } = true;
    /// <summary>True opens the line-by-line popup on each receipt until it's been gone through once.</summary>
    public bool ManualConfirmation { get; init; }
}

public sealed record ReceiptRecord(
    long Id, string Supplier, string FileHash, string? TransactionId, DateOnly? ReceiptDate, string ArchivePath, string PreviewPath,
    int PageWidth, int PageHeight, IReadOnlyList<OcrLine> Lines, IReadOnlyList<ReceiptEdit> Edits, ReceiptStatus Status, DateTime CreatedUtc);

public sealed record ReceiptSummary(long Id, string Supplier, DateOnly? ReceiptDate, ReceiptStatus Status, DateTime CreatedUtc, string? BatchPath);

public sealed record BatchRecord(long Id, long ReceiptId, string Path, int RowCount, string ByHandJson, int Unchanged, int Skipped);

/// <summary>Local SQLite store: one file, no server, and safe across power loss (WAL mode).</summary>
public sealed class PosStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _db;
    public string DataDir { get; }
    public string DbPath => Path.Combine(DataDir, "posscan.db");

    public PosStore(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException("This data was created by a newer version of POSscan. Please update POSscan.");
        if (version == SchemaVersion) return;
        if (version > 0) Backup(Path.Combine(DataDir, "backups", $"before-upgrade-v{version}-{DateTime.UtcNow:yyyyMMddHHmmss}.db"));
        using var tx = _db.BeginTransaction();
        Exec("""
            CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK (id = 1), json TEXT NOT NULL);
            CREATE TABLE receipts (
                id INTEGER PRIMARY KEY, supplier TEXT NOT NULL, file_hash TEXT NOT NULL, transaction_id TEXT,
                receipt_date TEXT, archive_path TEXT NOT NULL, preview_path TEXT NOT NULL,
                page_width INTEGER NOT NULL, page_height INTEGER NOT NULL,
                lines_json TEXT NOT NULL, edits_json TEXT NOT NULL, status TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE INDEX ix_receipts_hash ON receipts(file_hash);
            CREATE INDEX ix_receipts_txn ON receipts(transaction_id);
            CREATE TABLE item_map (
                supplier TEXT NOT NULL, receipt_code TEXT NOT NULL, sku TEXT NOT NULL, upc TEXT NOT NULL, estream_name TEXT NOT NULL,
                listing_code TEXT NOT NULL, container TEXT NOT NULL, not_for_resale INTEGER NOT NULL, confirmed_utc TEXT NOT NULL,
                PRIMARY KEY (supplier, receipt_code));
            CREATE TABLE price_history (
                id INTEGER PRIMARY KEY, supplier TEXT NOT NULL, receipt_code TEXT NOT NULL, cost TEXT NOT NULL,
                receipt_date TEXT NOT NULL, receipt_id INTEGER NOT NULL REFERENCES receipts(id));
            CREATE INDEX ix_history ON price_history(supplier, receipt_code, receipt_date);
            CREATE TABLE batches (
                id INTEGER PRIMARY KEY, receipt_id INTEGER NOT NULL REFERENCES receipts(id), path TEXT NOT NULL, sha256 TEXT NOT NULL,
                row_count INTEGER NOT NULL, profile TEXT NOT NULL, by_hand_json TEXT NOT NULL, unchanged INTEGER NOT NULL, skipped INTEGER NOT NULL,
                created_utc TEXT NOT NULL);
            CREATE TABLE audit_log (
                id INTEGER PRIMARY KEY, at_utc TEXT NOT NULL, receipt_id INTEGER, action TEXT NOT NULL, detail TEXT NOT NULL);
            """, tx);
        Exec($"PRAGMA user_version = {SchemaVersion}", tx);
        tx.Commit();
    }

    // ---- Settings ----
    public AppSettings GetSettings()
    {
        var json = Scalar("SELECT json FROM settings WHERE id = 1") as string;
        return json is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(json)!;
    }

    public void SaveSettings(AppSettings s)
    {
        Exec("INSERT INTO settings (id, json) VALUES (1, $j) ON CONFLICT(id) DO UPDATE SET json = $j", null, ("$j", JsonSerializer.Serialize(s)));
        Audit(null, "settings", JsonSerializer.Serialize(s));
    }

    // ---- Receipts ----
    public long AddReceipt(string supplier, string hash, string? txn, DateOnly? date, string archive, string preview,
        int width, int height, IReadOnlyList<OcrLine> lines)
    {
        Exec("""
            INSERT INTO receipts (supplier, file_hash, transaction_id, receipt_date, archive_path, preview_path, page_width, page_height, lines_json, edits_json, status, created_utc)
            VALUES ($s, $h, $t, $d, $a, $p, $w, $ht, $l, '[]', 'Draft', $c)
            """, null, ("$s", supplier), ("$h", hash), ("$t", (object?)txn ?? DBNull.Value), ("$d", (object?)date?.ToString("yyyy-MM-dd") ?? DBNull.Value),
            ("$a", archive), ("$p", preview), ("$w", width), ("$ht", height), ("$l", JsonSerializer.Serialize(lines)), ("$c", Now()));
        var id = (long)Scalar("SELECT last_insert_rowid()")!;
        Audit(id, "imported", Path.GetFileName(archive));
        return id;
    }

    public ReceiptRecord GetReceipt(long id)
    {
        using var cmd = Cmd("""
            SELECT id, supplier, file_hash, transaction_id, receipt_date, archive_path, preview_path, page_width, page_height,
                   lines_json, edits_json, status, created_utc FROM receipts WHERE id = $id
            """, null, ("$id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"Receipt {id} wasn't found.");
        return new ReceiptRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : DateOnly.ParseExact(r.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetInt32(8),
            JsonSerializer.Deserialize<List<OcrLine>>(r.GetString(9))!, JsonSerializer.Deserialize<List<ReceiptEdit>>(r.GetString(10))!,
            Enum.Parse<ReceiptStatus>(r.GetString(11)), DateTime.Parse(r.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public long? FindByHash(string hash) => FindOne("file_hash", hash);
    public long? FindByTransaction(string txn) => FindOne("transaction_id", txn);

    private long? FindOne(string column, string value) =>
        Scalar($"SELECT id FROM receipts WHERE {column} = $v AND status <> 'Voided' ORDER BY id LIMIT 1", null, ("$v", value)) as long?;

    public void SaveEdits(long id, IReadOnlyList<ReceiptEdit> edits, ReceiptEdit latest)
    {
        using var tx = _db.BeginTransaction();
        Exec("UPDATE receipts SET edits_json = $e WHERE id = $id", tx, ("$e", JsonSerializer.Serialize(edits)), ("$id", id));
        Audit(id, "edit", JsonSerializer.Serialize(latest), tx);
        tx.Commit();
    }

    public void SetStatus(long id, ReceiptStatus status)
    {
        Exec("UPDATE receipts SET status = $s WHERE id = $id", null, ("$s", status.ToString()), ("$id", id));
        Audit(id, "status", status.ToString());
    }

    public IReadOnlyList<ReceiptSummary> Recent(int limit = 20)
    {
        using var cmd = Cmd("""
            SELECT r.id, r.supplier, r.receipt_date, r.status, r.created_utc,
                   (SELECT path FROM batches b WHERE b.receipt_id = r.id ORDER BY b.id DESC LIMIT 1)
            FROM receipts r WHERE r.status <> 'Voided' ORDER BY r.id DESC LIMIT $n
            """, null, ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<ReceiptSummary>();
        while (r.Read())
            list.Add(new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Enum.Parse<ReceiptStatus>(r.GetString(3)), DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    // ---- Item map ----
    public List<ItemMapping> GetMappings(string supplier)
    {
        using var cmd = Cmd("SELECT receipt_code, sku, upc, estream_name, listing_code, container, not_for_resale FROM item_map WHERE supplier = $s", null, ("$s", supplier));
        using var r = cmd.ExecuteReader();
        var list = new List<ItemMapping>();
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6) != 0));
        return list;
    }

    public void SaveMapping(string supplier, ItemMapping m, long? receiptId = null)
    {
        Exec("""
            INSERT INTO item_map (supplier, receipt_code, sku, upc, estream_name, listing_code, container, not_for_resale, confirmed_utc)
            VALUES ($s, $c, $k, $u, $n, $l, $o, $f, $t)
            ON CONFLICT(supplier, receipt_code) DO UPDATE SET sku = $k, upc = $u, estream_name = $n, listing_code = $l, container = $o, not_for_resale = $f, confirmed_utc = $t
            """, null, ("$s", supplier), ("$c", m.ReceiptCode), ("$k", m.Sku), ("$u", m.Upc), ("$n", m.EstreamName), ("$l", m.ListingCode),
            ("$o", m.Container), ("$f", m.NotForResale ? 1 : 0), ("$t", Now()));
        Audit(receiptId, "mapping", JsonSerializer.Serialize(m));
    }

    public void DeleteMapping(string supplier, string code, long? receiptId = null)
    {
        Exec("DELETE FROM item_map WHERE supplier = $s AND receipt_code = $c", null, ("$s", supplier), ("$c", code));
        Audit(receiptId, "mapping-removed", code);
    }

    // ---- Price history ----
    public PriceLookup LookupPrice(string supplier, string code, DateOnly? receiptDate, long receiptId)
    {
        var date = (receiptDate ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd");
        using var cmd = Cmd("""
            SELECT cost, receipt_date FROM price_history
            WHERE supplier = $s AND receipt_code = $c AND receipt_id <> $r AND receipt_date <= $d
            ORDER BY receipt_date DESC, id DESC LIMIT 1
            """, null, ("$s", supplier), ("$c", code), ("$r", receiptId), ("$d", date));
        decimal? cost = null; DateOnly? on = null;
        using (var r = cmd.ExecuteReader())
            if (r.Read())
            {
                cost = decimal.Parse(r.GetString(0), CultureInfo.InvariantCulture);
                on = DateOnly.ParseExact(r.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        var newer = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM price_history WHERE supplier = $s AND receipt_code = $c AND receipt_id <> $r AND receipt_date > $d",
            null, ("$s", supplier), ("$c", code), ("$r", receiptId), ("$d", date)), CultureInfo.InvariantCulture) > 0;
        return new(cost, on, newer);
    }

    // ---- Export ----
    public long RecordExport(long receiptId, string supplier, DateOnly? date, string path, string sha256, int rows, string profile,
        IEnumerable<(string Code, decimal Cost)> costs, string byHandJson, int unchanged, int skipped)
    {
        using var tx = _db.BeginTransaction();
        Exec("""
            INSERT INTO batches (receipt_id, path, sha256, row_count, profile, by_hand_json, unchanged, skipped, created_utc)
            VALUES ($r, $p, $h, $n, $f, $b, $u, $k, $c)
            """, tx, ("$r", receiptId), ("$p", path), ("$h", sha256), ("$n", rows), ("$f", profile), ("$b", byHandJson), ("$u", unchanged), ("$k", skipped), ("$c", Now()));
        var batch = (long)Scalar("SELECT last_insert_rowid()", tx)!;
        var when = (date ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd");
        Exec("DELETE FROM price_history WHERE receipt_id = $r", tx, ("$r", receiptId));
        foreach (var (code, cost) in costs)
            Exec("INSERT INTO price_history (supplier, receipt_code, cost, receipt_date, receipt_id) VALUES ($s, $c, $v, $d, $r)", tx,
                ("$s", supplier), ("$c", code), ("$v", cost.ToString(CultureInfo.InvariantCulture)), ("$d", when), ("$r", receiptId));
        Exec("UPDATE receipts SET status = 'Exported' WHERE id = $r", tx, ("$r", receiptId));
        Audit(receiptId, "exported", $"{path} ({rows} rows, {sha256})", tx);
        tx.Commit();
        return batch;
    }

    public BatchRecord? LatestBatch(long receiptId)
    {
        using var cmd = Cmd("SELECT id, receipt_id, path, row_count, by_hand_json, unchanged, skipped FROM batches WHERE receipt_id = $r ORDER BY id DESC LIMIT 1", null, ("$r", receiptId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6)) : null;
    }

    public List<string> AuditActions(long receiptId)
    {
        using var cmd = Cmd("SELECT action FROM audit_log WHERE receipt_id = $r ORDER BY id", null, ("$r", receiptId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void Audit(long? receiptId, string action, string detail, SqliteTransaction? tx = null) =>
        Exec("INSERT INTO audit_log (at_utc, receipt_id, action, detail) VALUES ($t, $r, $a, $d)", tx,
            ("$t", Now()), ("$r", (object?)receiptId ?? DBNull.Value), ("$a", action), ("$d", detail));

    // ---- Backup ----
    public void Backup(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        Exec("VACUUM INTO $p", null, ("$p", path));
    }

    /// <summary>One backup a day, keeping the newest 30.</summary>
    public void BackupIfDue(DateTime? today = null)
    {
        var dir = Path.Combine(DataDir, "backups");
        var day = today ?? DateTime.Today;
        var path = Path.Combine(dir, $"daily-{day:yyyy-MM-dd}.db");
        if (File.Exists(path)) return;
        Backup(path);
        foreach (var old in Directory.GetFiles(dir, "daily-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(30))
            File.Delete(old);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private SqliteCommand Cmd(string sql, SqliteTransaction? tx, params (string, object)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private void Exec(string sql, SqliteTransaction? tx = null, params (string, object)[] args)
    {
        using var cmd = Cmd(sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, SqliteTransaction? tx = null, params (string, object)[] args)
    {
        using var cmd = Cmd(sql, tx, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }
}
