using System;
using Jellyfin.Plugin.YouTube.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Streaming;

/// <summary>
/// Listens to PlaybackStop events from Jellyfin. If the played item corresponds
/// to a YouTube video (filename matches {videoId}.strm pattern), marks it
/// as watched in the plugin's SQLite.
///
/// NOTE: Full event subscription requires ISessionManager which is injected
/// via Jellyfin's DI in v0.0.0.2. This scaffold exposes only the MarkWatched
/// method for external callers. Does NOT modify Jellyfin's main DB - that's
/// handled by Jellyfin's normal behavior for any library item.
/// </summary>
public class WatchedTracker : IDisposable
{
    private readonly SQLiteStore _db;
    private readonly ILogger _logger;
    private bool _disposed;

    public WatchedTracker(SQLiteStore db, ILogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Start()
    {
        _logger.LogInformation("WatchedTracker: scaffold started (event wiring pending next iteration)");
    }

    /// <summary>
    /// Marks the video as watched for the given user.
    /// </summary>
    public void MarkWatched(string videoId, string userId, long positionSeconds)
    {
        _db.MarkWatched(videoId, userId, positionSeconds);
        _logger.LogInformation("WatchedTracker: marked {VideoId} watched for user {User}", videoId, userId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
