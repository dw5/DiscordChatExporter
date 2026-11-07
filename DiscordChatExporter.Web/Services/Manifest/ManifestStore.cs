using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Web.Services.Manifest;

// One SQLite-backed manifest per export run. A single background writer owns the write connection
// and commits message inserts in batches; reads use their own short-lived connections (WAL allows
// concurrent readers alongside the one writer). Parallel channel exports only ever enqueue writes.
public sealed class ManifestStore : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly Channel<WriteOp> _writes = Channel.CreateUnbounded<WriteOp>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly Task _writerTask;
    private readonly SqliteConnection _writeConnection;

    private ManifestStore(string dbPath)
    {
        _dbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        _writeConnection = new SqliteConnection(_connectionString);
        _writeConnection.Open();
        Execute(_writeConnection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        CreateSchema(_writeConnection);

        _writerTask = Task.Run(RunWriterAsync);
    }

    public string DbPath => _dbPath;

    public static ManifestStore Create(string dbPath) => new(dbPath);

    // ---- Schema -------------------------------------------------------------------------------

    private static void CreateSchema(SqliteConnection conn) =>
        Execute(
            conn,
            """
            CREATE TABLE IF NOT EXISTS run (
                run_id TEXT PRIMARY KEY, guild_id TEXT, guild_name TEXT, me_user_id TEXT,
                token_kind TEXT, started_at INTEGER, finished_at INTEGER, formats TEXT,
                manifest_revision INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS channel (
                id TEXT PRIMARY KEY, parent_id TEXT, name TEXT, hierarchical_name TEXT,
                path TEXT, kind INTEGER, is_thread INTEGER, is_archived INTEGER,
                status TEXT NOT NULL DEFAULT 'Pending', message_count INTEGER NOT NULL DEFAULT 0,
                error TEXT, failed_attachment_count INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS message (
                id TEXT PRIMARY KEY, channel_id TEXT, author_id TEXT, author_name TEXT,
                timestamp INTEGER, kind INTEGER, is_pinned INTEGER, content TEXT,
                attachment_count INTEGER, embed_count INTEGER,
                keep INTEGER NOT NULL DEFAULT 0,
                delete_status TEXT NOT NULL DEFAULT 'None', delete_error TEXT, deleted_at INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_message_channel ON message(channel_id);
            CREATE INDEX IF NOT EXISTS ix_message_order ON message(timestamp, id);
            CREATE TABLE IF NOT EXISTS failed_asset (
                message_id TEXT, channel_id TEXT, url TEXT, retried_at INTEGER, local_path TEXT,
                PRIMARY KEY (message_id, url)
            );
            CREATE TABLE IF NOT EXISTS plan (
                id TEXT PRIMARY KEY, created_at INTEGER, criteria_json TEXT, user_id TEXT,
                token_kind TEXT, manifest_revision INTEGER
            );
            CREATE TABLE IF NOT EXISTS plan_item (
                plan_id TEXT, message_id TEXT, PRIMARY KEY (plan_id, message_id)
            );
            """
        );

    // ---- Background writer --------------------------------------------------------------------

    private async Task RunWriterAsync()
    {
        while (await _writes.Reader.WaitToReadAsync())
        {
            var batch = new List<WriteOp>();
            while (_writes.Reader.TryRead(out var op))
                batch.Add(op);

            var pendingMessages = new List<MessageRecord>();

            void FlushMessages()
            {
                if (pendingMessages.Count == 0)
                    return;

                using var tx = _writeConnection.BeginTransaction();
                foreach (var message in pendingMessages)
                    InsertMessage(_writeConnection, tx, message);
                tx.Commit();
                pendingMessages.Clear();
            }

            foreach (var op in batch)
            {
                if (op.Message is not null)
                {
                    pendingMessages.Add(op.Message);
                    continue;
                }

                FlushMessages();

                try
                {
                    using var tx = _writeConnection.BeginTransaction();
                    op.Action!(_writeConnection, tx);
                    tx.Commit();
                    op.Completion?.TrySetResult();
                }
                catch (Exception ex)
                {
                    op.Completion?.TrySetException(ex);
                }
            }

            try
            {
                FlushMessages();
            }
            catch
            {
                // Individual message inserts are best-effort; a failure here must not stop the writer
            }
        }
    }

    private static void InsertMessage(SqliteConnection conn, SqliteTransaction tx, MessageRecord m)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO message
                (id, channel_id, author_id, author_name, timestamp, kind, is_pinned, content,
                 attachment_count, embed_count, keep, delete_status)
            VALUES ($id,$cid,$aid,$aname,$ts,$kind,$pin,$content,$att,$emb,
                COALESCE((SELECT keep FROM message WHERE id=$id),0),
                COALESCE((SELECT delete_status FROM message WHERE id=$id),'None'))
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$cid", m.ChannelId);
        cmd.Parameters.AddWithValue("$aid", m.AuthorId);
        cmd.Parameters.AddWithValue("$aname", m.AuthorName);
        cmd.Parameters.AddWithValue("$ts", m.Timestamp.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$kind", m.Kind);
        cmd.Parameters.AddWithValue("$pin", m.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$content", m.Content);
        cmd.Parameters.AddWithValue("$att", m.AttachmentCount);
        cmd.Parameters.AddWithValue("$emb", m.EmbedCount);
        cmd.ExecuteNonQuery();
    }

    // ---- Write API (enqueued) -----------------------------------------------------------------

    public void EnqueueMessage(MessageRecord message) =>
        _writes.Writer.TryWrite(new WriteOp(Message: message));

    private Task EnqueueActionAsync(Action<SqliteConnection, SqliteTransaction> action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _writes.Writer.TryWrite(new WriteOp(Action: action, Completion: completion));
        return completion.Task;
    }

    public Task InitRunAsync(RunRecord run) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO run
                        (run_id, guild_id, guild_name, me_user_id, token_kind, started_at,
                         finished_at, formats, manifest_revision)
                    VALUES ($rid,$gid,$gname,$me,$tk,$start,$fin,$fmt,$rev)
                    """;
                cmd.Parameters.AddWithValue("$rid", run.RunId);
                cmd.Parameters.AddWithValue("$gid", run.GuildId);
                cmd.Parameters.AddWithValue("$gname", run.GuildName);
                cmd.Parameters.AddWithValue("$me", run.MeUserId);
                cmd.Parameters.AddWithValue("$tk", run.TokenKind);
                cmd.Parameters.AddWithValue("$start", run.StartedAt.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue(
                    "$fin",
                    (object?)run.FinishedAt?.ToUnixTimeMilliseconds() ?? DBNull.Value
                );
                cmd.Parameters.AddWithValue("$fmt", run.Formats);
                cmd.Parameters.AddWithValue("$rev", run.ManifestRevision);
                cmd.ExecuteNonQuery();
            }
        );

    public Task UpsertChannelAsync(ChannelRecord channel) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO channel
                        (id, parent_id, name, hierarchical_name, path, kind, is_thread,
                         is_archived, status, message_count, error, failed_attachment_count)
                    VALUES ($id,$pid,$name,$hname,$path,$kind,$thread,$arch,$status,$count,$err,$fac)
                    """;
                cmd.Parameters.AddWithValue("$id", channel.Id);
                cmd.Parameters.AddWithValue("$pid", (object?)channel.ParentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$name", channel.Name);
                cmd.Parameters.AddWithValue("$hname", channel.HierarchicalName);
                cmd.Parameters.AddWithValue("$path", channel.Path);
                cmd.Parameters.AddWithValue("$kind", channel.Kind);
                cmd.Parameters.AddWithValue("$thread", channel.IsThread ? 1 : 0);
                cmd.Parameters.AddWithValue("$arch", channel.IsArchived ? 1 : 0);
                cmd.Parameters.AddWithValue("$status", channel.Status.ToString());
                cmd.Parameters.AddWithValue("$count", channel.MessageCount);
                cmd.Parameters.AddWithValue("$err", (object?)channel.Error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fac", channel.FailedAttachmentCount);
                cmd.ExecuteNonQuery();
            }
        );

    public Task SetChannelStatusAsync(
        string channelId,
        ChannelExportStatus status,
        long messageCount,
        string? error,
        int failedAttachmentCount
    ) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE channel SET status=$status, message_count=$count, error=$err,
                        failed_attachment_count=$fac WHERE id=$id
                    """;
                cmd.Parameters.AddWithValue("$status", status.ToString());
                cmd.Parameters.AddWithValue("$count", messageCount);
                cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fac", failedAttachmentCount);
                cmd.Parameters.AddWithValue("$id", channelId);
                cmd.ExecuteNonQuery();
                BumpRevision(conn, tx);
            }
        );

    public Task RecordFailedAssetAsync(string messageId, string channelId, string url) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO failed_asset (message_id, channel_id, url)
                    VALUES ($mid,$cid,$url)
                    """;
                cmd.Parameters.AddWithValue("$mid", messageId);
                cmd.Parameters.AddWithValue("$cid", channelId);
                cmd.Parameters.AddWithValue("$url", url);
                cmd.ExecuteNonQuery();
            }
        );

    public Task SetFailedAssetRecoveredAsync(string url, string localPath) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "UPDATE failed_asset SET local_path=$lp, retried_at=$now WHERE url=$url";
                cmd.Parameters.AddWithValue("$lp", localPath);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$url", url);
                cmd.ExecuteNonQuery();
            }
        );

    public Task SetFinishedAsync(DateTimeOffset finishedAt) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE run SET finished_at=$fin";
                cmd.Parameters.AddWithValue("$fin", finishedAt.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
        );

    public Task SetKeepAsync(string messageId, bool keep) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE message SET keep=$keep WHERE id=$id";
                cmd.Parameters.AddWithValue("$keep", keep ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", messageId);
                cmd.ExecuteNonQuery();
                BumpRevision(conn, tx);
            }
        );

    public Task SetMessageDeleteStatusAsync(
        string messageId,
        MessageDeleteStatus status,
        string? error
    ) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE message SET delete_status=$st, delete_error=$err,
                        deleted_at=CASE WHEN $st IN ('Deleted','Gone') THEN $now ELSE deleted_at END
                    WHERE id=$id
                    """;
                cmd.Parameters.AddWithValue("$st", status.ToString());
                cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$id", messageId);
                cmd.ExecuteNonQuery();
            }
        );

    public Task CreatePlanAsync(
        string planId,
        string criteriaJson,
        string userId,
        string tokenKind,
        long manifestRevision,
        IReadOnlyList<string> candidateIds
    ) =>
        EnqueueActionAsync(
            (conn, tx) =>
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO plan
                            (id, created_at, criteria_json, user_id, token_kind, manifest_revision)
                        VALUES ($id,$now,$crit,$uid,$tk,$rev)
                        """;
                    cmd.Parameters.AddWithValue("$id", planId);
                    cmd.Parameters.AddWithValue(
                        "$now",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    );
                    cmd.Parameters.AddWithValue("$crit", criteriaJson);
                    cmd.Parameters.AddWithValue("$uid", userId);
                    cmd.Parameters.AddWithValue("$tk", tokenKind);
                    cmd.Parameters.AddWithValue("$rev", manifestRevision);
                    cmd.ExecuteNonQuery();
                }

                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM plan_item WHERE plan_id=$id";
                    del.Parameters.AddWithValue("$id", planId);
                    del.ExecuteNonQuery();
                }

                foreach (var id in candidateIds)
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText =
                        "INSERT OR IGNORE INTO plan_item (plan_id, message_id) VALUES ($pid,$mid)";
                    ins.Parameters.AddWithValue("$pid", planId);
                    ins.Parameters.AddWithValue("$mid", id);
                    ins.ExecuteNonQuery();
                }
            }
        );

    private static void BumpRevision(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE run SET manifest_revision = manifest_revision + 1";
        cmd.ExecuteNonQuery();
    }

    // Flush all currently-queued writes and wait until they are committed.
    public Task FlushAsync() => EnqueueActionAsync((_, _) => { });

    // ---- Read API (own connection) ------------------------------------------------------------

    private SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public RunRecord? GetRun()
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM run LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new RunRecord(
            reader.GetString(reader.GetOrdinal("run_id")),
            reader.GetString(reader.GetOrdinal("guild_id")),
            reader.GetString(reader.GetOrdinal("guild_name")),
            reader.GetString(reader.GetOrdinal("me_user_id")),
            reader.GetString(reader.GetOrdinal("token_kind")),
            DateTimeOffset.FromUnixTimeMilliseconds(
                reader.GetInt64(reader.GetOrdinal("started_at"))
            ),
            reader.IsDBNull(reader.GetOrdinal("finished_at"))
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("finished_at"))
                ),
            reader.GetString(reader.GetOrdinal("formats")),
            reader.GetInt64(reader.GetOrdinal("manifest_revision"))
        );
    }

    public long GetManifestRevision()
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT manifest_revision FROM run LIMIT 1";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<ChannelRecord> GetChannels()
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM channel ORDER BY path";
        using var reader = cmd.ExecuteReader();
        var list = new List<ChannelRecord>();
        while (reader.Read())
            list.Add(ReadChannel(reader));
        return list;
    }

    private static ChannelRecord ReadChannel(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("id")),
            reader.IsDBNull(reader.GetOrdinal("parent_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("parent_id")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.GetString(reader.GetOrdinal("hierarchical_name")),
            reader.GetString(reader.GetOrdinal("path")),
            reader.GetInt32(reader.GetOrdinal("kind")),
            reader.GetInt64(reader.GetOrdinal("is_thread")) != 0,
            reader.GetInt64(reader.GetOrdinal("is_archived")) != 0,
            Enum.Parse<ChannelExportStatus>(reader.GetString(reader.GetOrdinal("status"))),
            reader.GetInt64(reader.GetOrdinal("message_count")),
            reader.IsDBNull(reader.GetOrdinal("error"))
                ? null
                : reader.GetString(reader.GetOrdinal("error")),
            reader.GetInt32(reader.GetOrdinal("failed_attachment_count"))
        );

    public IReadOnlyList<MessageRecord> GetAllMessages()
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM message";
        using var reader = cmd.ExecuteReader();
        var list = new List<MessageRecord>();
        while (reader.Read())
            list.Add(ReadMessage(reader));
        return list;
    }

    public (IReadOnlyList<MessageRecord> Items, int NextCursor, int Total) QueryMessages(
        string? channelId,
        string? authorId,
        string? search,
        bool? keep,
        int cursor,
        int limit
    )
    {
        using var conn = OpenRead();
        var where = new List<string>();
        if (!string.IsNullOrEmpty(channelId))
            where.Add("channel_id=$cid");
        if (!string.IsNullOrEmpty(authorId))
            where.Add("author_id=$aid");
        if (!string.IsNullOrEmpty(search))
            where.Add("content LIKE $q");
        if (keep is not null)
            where.Add("keep=$keep");
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        int total;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM message {whereSql}";
            BindMessageFilters(countCmd, channelId, authorId, search, keep);
            total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT * FROM message {whereSql} ORDER BY timestamp, id LIMIT $limit OFFSET $offset";
        BindMessageFilters(cmd, channelId, authorId, search, keep);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", cursor);

        using var reader = cmd.ExecuteReader();
        var items = new List<MessageRecord>();
        while (reader.Read())
            items.Add(ReadMessage(reader));

        var next = cursor + items.Count;
        return (items, next < total ? next : -1, total);
    }

    private static void BindMessageFilters(
        SqliteCommand cmd,
        string? channelId,
        string? authorId,
        string? search,
        bool? keep
    )
    {
        if (!string.IsNullOrEmpty(channelId))
            cmd.Parameters.AddWithValue("$cid", channelId);
        if (!string.IsNullOrEmpty(authorId))
            cmd.Parameters.AddWithValue("$aid", authorId);
        if (!string.IsNullOrEmpty(search))
            cmd.Parameters.AddWithValue("$q", "%" + search + "%");
        if (keep is not null)
            cmd.Parameters.AddWithValue("$keep", keep.Value ? 1 : 0);
    }

    private static MessageRecord ReadMessage(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("channel_id")),
            reader.GetString(reader.GetOrdinal("author_id")),
            reader.GetString(reader.GetOrdinal("author_name")),
            DateTimeOffset.FromUnixTimeMilliseconds(
                reader.GetInt64(reader.GetOrdinal("timestamp"))
            ),
            reader.GetInt32(reader.GetOrdinal("kind")),
            reader.GetInt64(reader.GetOrdinal("is_pinned")) != 0,
            reader.GetString(reader.GetOrdinal("content")),
            reader.GetInt32(reader.GetOrdinal("attachment_count")),
            reader.GetInt32(reader.GetOrdinal("embed_count")),
            reader.GetInt64(reader.GetOrdinal("keep")) != 0,
            Enum.Parse<MessageDeleteStatus>(reader.GetString(reader.GetOrdinal("delete_status")))
        );

    public IReadOnlyList<string> GetPlanItemIds(string planId)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT message_id FROM plan_item WHERE plan_id=$id";
        cmd.Parameters.AddWithValue("$id", planId);
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    public PlanRecord? GetPlan(string planId)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM plan WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", planId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new PlanRecord(
            reader.GetString(reader.GetOrdinal("id")),
            DateTimeOffset.FromUnixTimeMilliseconds(
                reader.GetInt64(reader.GetOrdinal("created_at"))
            ),
            reader.GetString(reader.GetOrdinal("criteria_json")),
            reader.GetString(reader.GetOrdinal("user_id")),
            reader.GetString(reader.GetOrdinal("token_kind")),
            reader.GetInt64(reader.GetOrdinal("manifest_revision"))
        );
    }

    public MessageRecord? GetMessage(string messageId)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM message WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", messageId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMessage(reader) : null;
    }

    public IReadOnlyList<FailedAssetRecord> GetFailedAssets(bool onlyUnrecovered)
    {
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT message_id, channel_id, url, local_path FROM failed_asset"
            + (onlyUnrecovered ? " WHERE local_path IS NULL" : "");
        using var reader = cmd.ExecuteReader();
        var list = new List<FailedAssetRecord>();
        while (reader.Read())
        {
            list.Add(
                new FailedAssetRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)
                )
            );
        }
        return list;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        try
        {
            await _writerTask;
        }
        catch
        {
            // Writer faults are surfaced per-op; nothing to do on shutdown
        }
        _writeConnection.Dispose();
    }

    private sealed record WriteOp(
        MessageRecord? Message = null,
        Action<SqliteConnection, SqliteTransaction>? Action = null,
        TaskCompletionSource? Completion = null
    );
}

public record PlanRecord(
    string Id,
    DateTimeOffset CreatedAt,
    string CriteriaJson,
    string UserId,
    string TokenKind,
    long ManifestRevision
);

public record FailedAssetRecord(string MessageId, string ChannelId, string Url, string? LocalPath);
