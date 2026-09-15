using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTube.Configuration;

/// <summary>
/// Plugin configuration persisted in Jellyfin's plugin config XML.
/// Channel-level configuration (URL, polling interval, retention) is stored in
/// the plugin's own SQLite database — not here. This only holds global settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// YouTube Data API v3 key. Required for listing channel videos and metadata.
    /// Free quota: 10,000 units/day.
    /// </summary>
    public string YouTubeApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Path to yt-dlp binary. If empty, plugin assumes 'yt-dlp' is in PATH.
    /// On Raspberry Pi Docker: typically '/usr/local/bin/yt-dlp'.
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Root directory where .strm files are written. The user must add this path
    /// as a "Series" library in Jellyfin. Default: /config/youtube_plugin
    /// (works inside Docker Jellyfin containers where /config is the data volume).
    /// </summary>
    public string StrmRootPath { get; set; } = "/config/youtube_plugin";

    /// <summary>
    /// Port for the local stream proxy. Must not conflict with Jellyfin's ports
    /// (8096 default). Default 8585.
    /// </summary>
    public int StreamProxyPort { get; set; } = 8585;

    /// <summary>
    /// Maximum stream quality (yt-dlp -f format). Default: best[height<=1080].
    /// </summary>
    public string MaxQuality { get; set; } = "best[height<=1080]";

    /// <summary>
    /// Cache duration (hours) for resolved stream URLs. YouTube URLs expire ~6h.
    /// </summary>
    public int StreamUrlCacheHours { get; set; } = 5;

    /// <summary>
    /// Default polling interval (hours) for newly added channels.
    /// </summary>
    public int DefaultPollingIntervalHours { get; set; } = 6;

    /// <summary>
    /// Default retention policy for newly added channels.
    /// Values: "permanent" | "delete_after_2_days"
    /// </summary>
    public string DefaultRetentionPolicy { get; set; } = "permanent";

    /// <summary>
    /// Whether the stream proxy is enabled. If false, .strm files contain
    /// direct (short-lived) YouTube URLs.
    /// </summary>
    public bool UseStreamProxy { get; set; } = true;
}
