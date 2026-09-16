using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Data;

/// <summary>
/// Own SQLite database for the plugin. NEVER touches Jellyfin's main jellyfin.db.
/// Schema versioned via PRAGMA user_version; migrations run on Initialize().
/// </summary>
public class SQLiteStore : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private SqliteConnection? _connection;
    private bool _disposed;

    private const int CurrentSchemaVersion = 1;

    public SQLiteStore(string dbPath, ILogger logger)
    {
        _connectionString = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate";
        _logger = logger;
    }

    public void Initialize()
    {
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();

        using var pragmaCmd = _connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA user_version;";
        pragmaCmd.ExecuteNonQuery();
        var userVersion = (long)(pragmaCmd.ExecuteScalar() ?? 0);

        if (userVersion < 1)
        {
            CreateSchemaV1();
            using var v = _connection.CreateCommand();
            v.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            v.ExecuteNonQuery();
            _logger.LogInformation("YouTube plugin: SQLite schema v1 created");
        }
    }

    private void CreateSchemaV1()
    {
        if (_connection == null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS channels (
                id TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                name TEXT NOT NULL,
                polling_interval_hours INTEGER NOT NULL DEFAULT 6,
                retention_policy TEXT NOT NULL DEFAULT 'permanent',
                added_at TEXT NOT NULL,
                last_sync_at TEXT,
                last_sync_status TEXT,
                video_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS videos (
                id TEXT PRIMARY KEY,
                channel_id TEXT NOT NULL,
                title TEXT NOT NULL,
                description TEXT,
                published_at TEXT NOT NULL,
                duration_seconds INTEGER NOT NULL DEFAULT 0,
                thumbnail_url TEXT,
                added_at TEXT NOT NULL,
                FOREIGN KEY (channel_id) REFERENCES channels(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS watched (
                video_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                watched_at TEXT NOT NULL,
                position_seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (video_id, user_id),
                FOREIGN KEY (video_id) REFERENCES videos(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS config (
                key TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS sync_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                status TEXT,
                error TEXT,
                videos_added INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_videos_channel ON videos(channel_id);
            CREATE INDEX IF NOT EXISTS idx_videos_published ON videos(published_at DESC);
            CREATE INDEX IF NOT EXISTS idx_watched_user ON watched(user_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection GetConnection()
    {
        if (_connection == null) throw new InvalidOperationException("DB not initialized");
        return _connection;
    }

    // ===== Channels =====

    public List<ChannelRow> GetAllChannels()
    {
        var list = new List<ChannelRow>();
        if (_connection == null) return list;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, url, name, polling_interval_hours, retention_policy, added_at, last_sync_at, last_sync_status, video_count FROM channels ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ChannelRow
            {
                Id = r.GetString(0),
                Url = r.GetString(1),
                Name = r.GetString(2),
                PollingIntervalHours = r.GetInt32(3),
                RetentionPolicy = r.GetString(4),
                AddedAt = DateTime.Parse(r.GetString(5)),
                LastSyncAt = r.IsDBNull(6) ? DateTime.MinValue : DateTime.Parse(r.GetString(6)),
                LastSyncStatus = r.IsDBNull(7) ? null : r.GetString(7),
                VideoCount = r.GetInt32(8)
            });
        }
        return list;
    }

    public ChannelRow? GetChannel(string id)
    {
        if (_connection == null) return null;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, url, name, polling_interval_hours, retention_policy, added_at, last_sync_at, last_sync_status, video_count FROM channels WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ChannelRow
        {
            Id = r.GetString(0),
            Url = r.GetString(1),
            Name = r.GetString(2),
            PollingIntervalHours = r.GetInt32(3),
            RetentionPolicy = r.GetString(4),
            AddedAt = DateTime.Parse(r.GetString(5)),
            LastSyncAt = r.IsDBNull(6) ? DateTime.MinValue : DateTime.Parse(r.GetString(6)),
            LastSyncStatus = r.IsDBNull(7) ? null : r.GetString(7),
            VideoCount = r.GetInt32(8)
        };
    }

    public void UpsertChannel(ChannelRow c)
    {
        if (_connection == null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO channels (id, url, name, polling_interval_hours, retention_policy, added_at, last_sync_at, last_sync_status, video_count)
            VALUES (@id, @url, @name, @poll, @retention, @added, @lastsync, @status, @count)
            ON CONFLICT(id) DO UPDATE SET
              url=@url, name=@name, polling_interval_hours=@poll, retention_policy=@retention,
              last_sync_at=@lastsync, last_sync_status=@status, video_count=@count
            """;
        cmd.Parameters.AddWithValue("@id", c.Id);
        cmd.Parameters.AddWithValue("@url", c.Url);
        cmd.Parameters.AddWithValue("@name", c.Name);
        cmd.Parameters.AddWithValue("@poll", c.PollingIntervalHours);
        cmd.Parameters.AddWithValue("@retention", c.RetentionPolicy);
        cmd.Parameters.AddWithValue("@added", c.AddedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@lastsync", c.LastSyncAt == DateTime.MinValue ? (object)DBNull.Value : c.LastSyncAt.ToString("O"));
        cmd.Parameters.AddWithValue("@status", (object?)c.LastSyncStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@count", c.VideoCount);
        cmd.ExecuteNonQuery();
    }

    public void DeleteChannel(string id)
    {
        if (_connection == null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM channels WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    // ===== Videos =====

    public List<VideoRow> GetVideosByChannel(string channelId, int limit = 100)
    {
        var list = new List<VideoRow>();
        if (_connection == null) return list;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, channel_id, title, description, published_at, duration_seconds, thumbnail_url, added_at FROM videos WHERE channel_id=@cid ORDER BY published_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@cid", channelId);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VideoRow
            {
                Id = r.GetString(0),
                ChannelId = r.GetString(1),
                Title = r.GetString(2),
                Description = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                PublishedAt = DateTime.Parse(r.GetString(4)),
                DurationSeconds = r.GetInt32(5),
                ThumbnailUrl = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                AddedAt = DateTime.Parse(r.GetString(7))
            });
        }
        return list;
    }

    public void UpsertVideo(VideoRow v)
    {
        if (_connection == null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO videos (id, channel_id, title, description, published_at, duration_seconds, thumbnail_url, added_at)
            VALUES (@id, @cid, @title, @desc, @pub, @dur, @thumb, @added)
            ON CONFLICT(id) DO UPDATE SET
              title=@title, description=@desc, published_at=@pub, duration_seconds=@dur, thumbnail_url=@thumb
            """;
        cmd.Parameters.AddWithValue("@id", v.Id);
        cmd.Parameters.AddWithValue("@cid", v.ChannelId);
        cmd.Parameters.AddWithValue("@title", v.Title);
        cmd.Parameters.AddWithValue("@desc", v.Description ?? string.Empty);
        cmd.Parameters.AddWithValue("@pub", v.PublishedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@dur", v.DurationSeconds);
        cmd.Parameters.AddWithValue("@thumb", v.ThumbnailUrl ?? string.Empty);
        cmd.Parameters.AddWithValue("@added", v.AddedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void DeleteVideo(string id)
    {
        if (_connection == null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM videos WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    // ===== Watched =====

    public void MarkWatched(string videoId, string userId, long positionSeconds)
    {
        if (_connection == null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO watched (video_id, user_id, watched_at, position_seconds)
            VALUES (@vid, @uid, @when, @pos)
            ON CONFLICT(video_id, user_id) DO UPDATE SET watched_at=@when, position_seconds=@pos
            """;
        cmd.Parameters.AddWithValue("@vid", videoId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@when", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@pos", positionSeconds);
        cmd.ExecuteNonQuery();
    }

    public List<WatchedRow> GetWatchedByUser(string userId)
    {
        var list = new List<WatchedRow>();
        if (_connection == null) return list;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT video_id, user_id, watched_at, position_seconds FROM watched WHERE user_id=@uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new WatchedRow
            {
                VideoId = r.GetString(0),
                UserId = r.GetString(1),
                WatchedAt = DateTime.Parse(r.GetString(2)),
                PositionSeconds = r.GetInt64(3)
            });
        }
        return list;
    }

    public List<string> GetVideosOlderThan(string channelId, int days)
    {
        var list = new List<string>();
        if (_connection == null) return list;
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("O");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT v.id FROM videos v
            INNER JOIN watched w ON w.video_id = v.id
            WHERE v.channel_id=@cid AND w.watched_at < @cutoff
            """;
        cmd.Parameters.AddWithValue("@cid", channelId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
    }
}
