using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTube.Configuration;

/// <summary>
/// Plugin configuration persisted in Jellyfin's plugin config XML.
/// Channel list is stored here so the UI can save/load it via the built-in
/// ApiClient.getPluginConfiguration/updatePluginConfiguration endpoints.
/// SQLite holds the heavier data (video catalog, watched state, sync log).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// YouTube Data API v3 key. Required for listing channel videos and metadata.
    /// Free quota: 10,000 units/day.
    /// </summary>
    public string YouTubeApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Path to yt-dlp binary. If empty or yt-dlp not found, plugin falls back
    /// to YoutubeExplode (no external binary needed).
    /// On Raspberry Pi Docker with host yt-dlp mounted: '/usr/local/bin/yt-dlp'.
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Root directory where .strm files are written. The user must add this path
    /// as a "Series" library in Jellyfin. Default: /config/youtube_plugin
    /// </summary>
    public string StrmRootPath { get; set; } = "/config/youtube_plugin";

    /// <summary>
    /// Port for the local stream proxy. Must not conflict with Jellyfin's ports
    /// (8096 default). Default 8585.
    /// </summary>
    public int StreamProxyPort { get; set; } = 8585;

    /// <summary>
    /// Maximum stream quality. YoutubeExplode selects the best muxed stream up to
    /// this height. yt-dlp uses this as -f format selector.
    /// </summary>
    public string MaxQuality { get; set; } = "1080";

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
    /// direct (short-lived) YouTube URLs (not recommended - they expire in ~6h).
    /// </summary>
    public bool UseStreamProxy { get; set; } = true;

    /// <summary>
    /// Cache duration (hours) for resolved stream URLs. YouTube URLs expire ~6h.
    /// Default 5h leaves a safety margin.
    /// </summary>
    public int StreamUrlCacheHours { get; set; } = 5;

    /// <summary>
    /// Prefer YoutubeExplode (no external binary) over yt-dlp.
    /// Default true - works out-of-the-box without installing anything.
    /// If false, always use yt-dlp (more robust to YouTube changes but requires binary).
    /// </summary>
    public bool PreferYoutubeExplode { get; set; } = true;

    /// <summary>
    /// Channel list managed from the configuration page. Each channel has its
    /// own polling interval and retention policy.
    /// </summary>
    public List<ChannelConfig> Channels { get; set; } = new();
}

/// <summary>
/// Per-channel configuration. Stored in PluginConfiguration.Channels.
/// </summary>
public class ChannelConfig
{
    public string Id { get; set; } = string.Empty;        // YouTube channel ID
    public string Url { get; set; } = string.Empty;       // Original URL added by user
    public string Name { get; set; } = string.Empty;      // Display name (resolved by API)
    public int PollingIntervalHours { get; set; } = 6;
    public string RetentionPolicy { get; set; } = "permanent"; // permanent | delete_after_2_days
    public DateTime AddedAt { get; set; }
    public DateTime LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public int VideoCount { get; set; }
    public bool Disabled { get; set; } = false;
}
