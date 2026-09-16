using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTube.Configuration;

/// <summary>
/// Plugin configuration persisted in Jellyfin's plugin config XML.
/// Channel list is stored here so the UI can save/load it via the built-in
/// ApiClient.getPluginConfiguration/updatePluginConfiguration endpoints.
/// SQLite holds the heavier data (video catalog, watched state, sync log).
///
/// No YouTube Data API v3 key required - yt-dlp handles all metadata.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Path to yt-dlp binary. REQUIRED for the plugin to work.
    /// Inside Docker with host yt-dlp mounted: '/usr/local/bin/yt-dlp'.
    /// If 'yt-dlp' is in PATH inside the container, leave as 'yt-dlp'.
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Root directory where .strm files are written. The user must add this path
    /// as a "Shows" library in Jellyfin. Default: /config/youtube_plugin
    /// </summary>
    public string StrmRootPath { get; set; } = "/config/youtube_plugin";

    /// <summary>
    /// Port for the local stream proxy. Default 8585.
    /// </summary>
    public int StreamProxyPort { get; set; } = 8585;

    /// <summary>
    /// Maximum stream quality (height). yt-dlp uses this in -f format selector,
    /// YoutubeExplode filters muxed streams by this height.
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
    /// Duración (horas) del cache de URLs de stream resueltas. Las URLs de YouTube expiran ~6h.
    /// </summary>
    public int StreamUrlCacheHours { get; set; } = 5;

    /// <summary>
    /// Lista de canales gestionados desde la página de configuración. Cada canal
    /// tiene su propio intervalo de polling y política de retención.
    /// </summary>
    public List<ChannelConfig> Channels { get; set; } = new();
}

/// <summary>
/// Per-channel configuration. Stored in PluginConfiguration.Channels.
/// </summary>
public class ChannelConfig
{
    /// <summary>
    /// YouTube channel ID (resolved by yt-dlp on first sync).
    /// Before first sync, this equals the URL the user entered.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int PollingIntervalHours { get; set; } = 6;
    public string RetentionPolicy { get; set; } = "permanent";
    public DateTime AddedAt { get; set; }
    public DateTime LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public int VideoCount { get; set; }
    public bool Disabled { get; set; } = false;
}
