using System;

namespace Jellyfin.Plugin.YouTube.Data;

public class ChannelRow
{
    public string Id { get; set; } = string.Empty; // YouTube channel ID
    public string Url { get; set; } = string.Empty; // Original URL added by user
    public string Name { get; set; } = string.Empty; // Channel display name
    public int PollingIntervalHours { get; set; } = 6;
    public string RetentionPolicy { get; set; } = "permanent"; // permanent | delete_after_2_days
    public DateTime AddedAt { get; set; }
    public DateTime LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public int VideoCount { get; set; }
}

public class VideoRow
{
    public string Id { get; set; } = string.Empty; // YouTube video ID
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
}

public class WatchedRow
{
    public string VideoId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime WatchedAt { get; set; }
    public long PositionSeconds { get; set; }
}

public class ConfigRow
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
