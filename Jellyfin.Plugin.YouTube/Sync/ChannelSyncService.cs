using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Sync;

/// <summary>
/// Background scheduler that periodically syncs each registered channel.
/// Uses System.Threading.Timer (built-in .NET, no external NuGet dep) instead
/// of Quartz - keeps the plugin assembly footprint minimal.
///
/// Source of truth for the channel list is PluginConfiguration.Channels
/// (persisted in Jellyfin's XML config). SQLite holds the heavier data
/// (videos, watched, log).
/// </summary>
public class ChannelSyncService : IDisposable
{
    private readonly PluginConfiguration _config;
    private readonly SQLiteStore _db;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
    private bool _disposed;

    public ChannelSyncService(PluginConfiguration config, SQLiteStore db, ILogger logger)
    {
        _config = config;
        _db = db;
        _logger = logger;
    }

    public Task StartAsync()
    {
        PluginServiceHost.SyncService = this;

        // Initial sync of all channels in the background
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await SyncAllChannelsAsync().ConfigureAwait(false);
        });

        // Schedule per-channel timers
        var channels = _config.Channels ?? new();
        foreach (var ch in channels.Where(c => !c.Disabled))
        {
            ScheduleChannel(ch);
        }
        _logger.LogInformation("YouTube plugin: scheduled {Count} channel sync jobs", channels.Count);

        return Task.CompletedTask;
    }

    public void ScheduleChannel(ChannelConfig channel)
    {
        if (channel.Disabled)
        {
            UnscheduleChannel(channel.Id);
            return;
        }

        // Remove existing timer if any
        if (_timers.TryRemove(channel.Id, out var existing))
        {
            existing.Dispose();
        }

        var intervalMs = Math.Max(60_000, channel.PollingIntervalHours * 3600_000);
        var timer = new Timer(async _ => await SafeSyncChannelAsync(channel.Id), null, Timeout.Infinite, intervalMs);
        timer.Change(intervalMs, intervalMs);
        _timers[channel.Id] = timer;
    }

    public void UnscheduleChannel(string channelId)
    {
        if (_timers.TryRemove(channelId, out var existing))
        {
            existing.Dispose();
        }
    }

    /// <summary>
    /// Called when the user saves configuration in the UI. Resyncs the timers
    /// to match the new channel list.
    /// </summary>
    public Task ReloadFromConfigAsync()
    {
        var channels = _config.Channels ?? new();
        var channelIds = channels.Select(c => c.Id).ToHashSet();

        // Unschedule channels that no longer exist
        foreach (var id in _timers.Keys.ToList())
        {
            if (!channelIds.Contains(id))
            {
                UnscheduleChannel(id);
            }
        }

        // Schedule any new/updated channel
        foreach (var ch in channels.Where(c => !c.Disabled))
        {
            ScheduleChannel(ch);
        }

        _logger.LogInformation("YouTube plugin: reloaded {Count} channel schedules", channels.Count);
        return Task.CompletedTask;
    }

    public async Task SyncAllChannelsAsync()
    {
        var channels = _config.Channels ?? new();
        foreach (var ch in channels.Where(c => !c.Disabled))
        {
            try { await SyncChannelAsync(ch).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Sync failed for channel {Id}", ch.Id); }
        }
    }

    private async Task SafeSyncChannelAsync(string channelId)
    {
        try
        {
            // Re-read the channel from current config (in case user changed settings)
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            var ch = plugin.Configuration.Channels?.FirstOrDefault(c => c.Id == channelId);
            if (ch == null || ch.Disabled) return;

            await SyncChannelAsync(ch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeSyncChannelAsync failed for {Id}", channelId);
        }
    }

    public async Task SyncChannelAsync(ChannelConfig channel)
    {
        _logger.LogInformation("YouTube plugin: syncing channel {Name} ({Id})", channel.Name, channel.Id);
        var strmWriter = new StrmWriter(_config.StrmRootPath, _config.UseStreamProxy, _config.StreamProxyPort, _logger);

        try
        {
            var client = new YtDlpChannelClient(_config.YtDlpPath, _logger);

            // Resolve channel metadata (id, name, uploads URL)
            ChannelInfo? info;
            try
            {
                info = client.ResolveChannel(channel.Url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resolve channel failed for {Url}", channel.Url);
                channel.LastSyncAt = DateTime.UtcNow;
                channel.LastSyncStatus = $"error: resolve failed: {ex.Message}";
                PersistChannel(channel);
                return;
            }
            if (info == null)
            {
                channel.LastSyncStatus = "error: channel not found (check yt-dlp path)";
                channel.LastSyncAt = DateTime.UtcNow;
                PersistChannel(channel);
                return;
            }

            // Update Id and Name if not yet resolved
            if (channel.Id != info.Id)
            {
                // Update the channel ID in the config
                channel.Id = info.Id;
            }
            if (!string.IsNullOrEmpty(info.Title) && info.Title != channel.Name)
            {
                // Rename: delete old .strm directory and create new
                if (!string.IsNullOrEmpty(channel.Name))
                {
                    strmWriter.DeleteChannelByName(channel.Name);
                }
                channel.Name = info.Title;
            }

            strmWriter.EnsureChannelDirectory(channel.Name);

            // Fetch latest videos via yt-dlp --flat-playlist
            var videos = client.ListChannelVideos(info.UploadsUrl, maxResults: 50);
            int added = 0;
            foreach (var v in videos)
            {
                var row = new VideoRow
                {
                    Id = v.Id,
                    ChannelId = channel.Id,
                    Title = v.Title,
                    Description = "",
                    PublishedAt = v.PublishedAt,
                    DurationSeconds = v.DurationSeconds,
                    ThumbnailUrl = v.ThumbnailUrl,
                    AddedAt = DateTime.UtcNow
                };
                _db.UpsertVideo(row);
                strmWriter.WriteStrm(channel, row);
                added++;
            }

            channel.VideoCount = videos.Count;
            channel.LastSyncAt = DateTime.UtcNow;
            channel.LastSyncStatus = $"ok: {added} videos";
            PersistChannel(channel);

            ApplyRetention(channel, strmWriter);

            _logger.LogInformation("YouTube plugin: sync complete for {Name}: {Count} videos", channel.Name, added);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "YouTube plugin: sync failed for {Name}", channel.Name);
            channel.LastSyncAt = DateTime.UtcNow;
            channel.LastSyncStatus = $"error: {ex.Message}";
            PersistChannel(channel);
        }
    }

    /// <summary>
    /// Persist channel state back to plugin config AND SQLite mirror.
    /// </summary>
    private void PersistChannel(ChannelConfig channel)
    {
        // The actual config persistence happens when the user saves from the UI.
        // For state changes from sync (LastSyncAt, etc.) we update in-memory
        // and mirror to SQLite for queryability.
        _db.UpsertChannel(new ChannelRow
        {
            Id = channel.Id,
            Url = channel.Url,
            Name = channel.Name,
            PollingIntervalHours = channel.PollingIntervalHours,
            RetentionPolicy = channel.RetentionPolicy,
            AddedAt = channel.AddedAt,
            LastSyncAt = channel.LastSyncAt,
            LastSyncStatus = channel.LastSyncStatus,
            VideoCount = channel.VideoCount
        });
    }

    private void ApplyRetention(ChannelConfig channel, StrmWriter strmWriter)
    {
        if (channel.RetentionPolicy == "permanent") return;
        if (channel.RetentionPolicy == "delete_after_2_days")
        {
            var oldVideoIds = _db.GetVideosOlderThan(channel.Id, days: 2);
            foreach (var vid in oldVideoIds)
            {
                strmWriter.DeleteStrm(channel.Name, vid);
                _db.DeleteVideo(vid);
            }
            if (oldVideoIds.Count > 0)
                _logger.LogInformation("YouTube plugin: retention deleted {Count} videos from {Name}", oldVideoIds.Count, channel.Name);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _timers)
        {
            try { kv.Value.Dispose(); } catch { /* ignore */ }
        }
        _timers.Clear();
    }
}

/// <summary>
/// Static host to allow the Plugin.UpdateConfiguration hook to reach the
/// live ChannelSyncService without DI complications.
/// </summary>
public static class PluginServiceHost
{
    public static ChannelSyncService? SyncService { get; set; }
    public static SQLiteStore? Database { get; set; }

    public static async Task RunChannelSyncAsync(string channelId)
    {
        if (SyncService == null) return;
        var plugin = Plugin.Instance;
        if (plugin == null) return;

        var ch = plugin.Configuration.Channels?.FirstOrDefault(c => c.Id == channelId);
        if (ch == null) return;

        await SyncService.SyncChannelAsync(ch).ConfigureAwait(false);
    }
}
