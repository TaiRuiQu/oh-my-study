using System.Security.Cryptography;
using System.Text;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    // Read the complete stored values, including unknown JSON fields and extension tables.
    // The v1->v2 migration adds empty tables and two nullable page columns; normalize only
    // those specified additions. This is a content proof, not a SQLite file-byte identity.
    internal Task<string> MigrationContentFingerprintAsync(CancellationToken ct) => Run(() =>
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void AddBytes(byte[] bytes) { hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
        void Add(string text) => AddBytes(Encoding.UTF8.GetBytes(text));
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        foreach (string table in Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0)))
        {
            ct.ThrowIfCancellationRequested();
            if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM " + Quote(table))) == 0) continue;
            var columns = Query("PRAGMA table_info(" + Quote(table) + ")", r => r.GetString(1));
            if (table == "source_pages")
                foreach (string added in new[] { "original_asset_id", "pdf_page_index" })
                    if (columns.Contains(added) && Convert.ToInt64(Scalar("SELECT COUNT(*) FROM " + Quote(table) + " WHERE " + Quote(added) + " IS NOT NULL")) == 0) columns.Remove(added);
            columns.Sort(StringComparer.Ordinal);
            Add(table); foreach (string column in columns) Add(column);
            using var command = connection.CreateCommand();
            string names = string.Join(',', columns.Select(Quote));
            command.CommandText = "SELECT " + names + " FROM " + Quote(table) + " ORDER BY " + names;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested(); Add("row");
                for (int index = 0; index < reader.FieldCount; index++)
                    switch (reader.GetValue(index))
                    {
                        case DBNull: Add("null"); break;
                        case long value: Add("integer"); AddBytes(BitConverter.GetBytes(value)); break;
                        case double value: Add("real"); AddBytes(BitConverter.GetBytes(value)); break;
                        case string value: Add("text"); Add(value); break;
                        case byte[] value: Add("blob"); AddBytes(value); break;
                        default: throw new InvalidDataException("资料包含无法核对的字段。");
                    }
            }
            Add("end-table");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }, ct);
}
