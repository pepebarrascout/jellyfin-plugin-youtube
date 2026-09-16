using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.YouTube.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Streaming;

/// <summary>
/// Listens to Jellyfin playback events via ISessionManager. When a playback
/// stops on an item whose path matches a YouTube .strm file, marks the video
/// as watched in the plugin's own SQLite.
///
/// NOTE: This does NOT modify Jellyfin's main DB. Jellyfin handles its own
/// PlaybackProgress for any library item; we only mirror that state into
/// our SQLite for retention policy decisions.
/// </summary>
public class WatchedTracker : IDisposable
{
    private readonly SQLiteStore _db;
    private readonly ILogger _logger;
    private readonly ISessionManager _sessionManager;
    private bool _disposed;

    // Match a .strm file like:
    //   /config/youtube_plugin/Some Channel/Season 01/abc123XYZ.strm
    // Group 1 = video id (filename without extension)
    private static readonly Regex StrmPathPattern = new(
        @"youtube_plugin[/\\].+[/\\]Season\s\d+[/\\]([A-Za-z0-9_-]{6,})\.strm$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public WatchedTracker(SQLiteStore db, ISessionManager sessionManager, ILogger logger)
    {
        _db = db;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public void Start()
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("WatchedTracker: suscrito a eventos PlaybackStopped");
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var item = e.Item;
            if (item == null) return;

            var path = item.Path ?? "";
            var match = StrmPathPattern.Match(path);
            if (!match.Success) return;

            var videoId = match.Groups[1].Value;
            var userId = e.Users?.FirstOrDefault()?.Id.ToString() ?? "desconocido";
            var positionSec = (long)((e.PlaybackPositionTicks ?? 0) / TimeSpan.TicksPerSecond);

            _db.MarkWatched(videoId, userId, positionSec);
            _logger.LogInformation(
                "WatchedTracker: marcado {VideoId} como visto por usuario {User} en posición {Pos}s",
                videoId, userId, positionSec);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WatchedTracker: error manejando evento PlaybackStopped");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
        catch { /* ignore */ }
    }
}
