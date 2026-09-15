using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Data;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;

namespace Jellyfin.Plugin.YouTube.Sync;

/// <summary>
/// Background scheduler that periodically syncs each registered channel.
/// Uses Quartz.NET for per-channel scheduling with independent intervals.
///
/// Source of truth for the channel list is PluginConfiguration.Channels (persisted
/// in Jellyfin's XML config). SQLite holds the heavier data (videos, watched, log).
/// </summary>
public class ChannelSyncService : IDisposable
{
    private readonly PluginConfiguration _config;
    private readonly SQLiteStore _db;
    private readonly ILogger _logger;
    private IScheduler? _scheduler;
    private bool _disposed;

    public ChannelSyncService(PluginConfiguration config, SQLiteStore db, ILogger logger)
    {
        _config = config;
        _db = db;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        var factory = new StdSchedulerFactory();
        _scheduler = await factory.GetScheduler().ConfigureAwait(false);
        await _scheduler.Start().ConfigureAwait(false);

        PluginServiceHost.SyncService = this;

        // Initial sync of all channels (in background, don't block startup)
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await SyncAllChannelsAsync().ConfigureAwait(false);
        });

        // Schedule per-channel jobs based on config
        var channels = _config.Channels ?? new();
        foreach (var ch in channels.Where(c => !c.Disabled))
        {
            await ScheduleChannelAsync(ch).ConfigureAwait(false);
        }
        _logger.LogInformation("YouTube plugin: scheduled {Count} channel sync jobs", channels.Count);
    }

    public async Task ScheduleChannelAsync(ChannelConfig channel)
    {
        if (_scheduler == null) return;
        var jobKey = new JobKey($"youtube-sync-{channel.Id}", "youtube");
        var triggerKey = new TriggerKey($"youtube-trigger-{channel.Id}", "youtube");

        await _scheduler.DeleteJob(jobKey).ConfigureAwait(false);
        if (channel.Disabled) return;

        var job = JobBuilder.Create<ChannelSyncJob>()
            .WithIdentity(jobKey)
            .UsingJobData("channelId", channel.Id)
            .Build();

        var interval = TimeSpan.FromHours(Math.Max(1, channel.PollingIntervalHours));
        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .StartNow()
            .WithSimpleSchedule(x => x.WithInterval(interval).RepeatForever())
            .Build();

        await _scheduler.ScheduleJob(job, trigger).ConfigureAwait(false);
    }

    public async Task UnscheduleChannelAsync(string channelId)
    {
        if (_scheduler == null) return;
        await _scheduler.DeleteJob(new JobKey($"youtube-sync-{channelId}", "youtube")).ConfigureAwait(false);
    }

    /// <summary>
    /// Called when the user saves configuration in the UI. Resyncs the scheduler
    /// to match the new channel list.
    /// </summary>
    public async Task ReloadFromConfigAsync()
    {
        if (_scheduler == null) return;
        var channels = _config.Channels ?? new();
        var channelIds = channels.Select(c => c.Id).ToHashSet();

        // Unschedule channels that no longer exist
        var existingJobs = await _scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("youtube")).ConfigureAwait(false);
        foreach (var jobKey in existingJobs.ToList())
        {
            var id = jobKey.Name.Replace("youtube-sync-", "");
            if (!channelIds.Contains(id))
            {
                await _scheduler.DeleteJob(jobKey).ConfigureAwait(false);
            }
        }

        // Schedule any new channel
        foreach (var ch in channels.Where(c => !c.Disabled))
        {
            await ScheduleChannelAsync(ch).ConfigureAwait(false);
        }

        _logger.LogInformation("YouTube plugin: reloaded {Count} channel schedules", channels.Count);
    }

    public async Task SyncAllChannelsAsync()
    {
        if (string.IsNullOrEmpty(_config.YouTubeApiKey))
        {
            _logger.LogWarning("YouTube plugin: cannot sync - API key not configured");
            return;
        }

        var channels = _config.Channels ?? new();
        foreach (var ch in channels.Where(c => !c.Disabled))
        {
            try { await SyncChannelAsync(ch).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Sync failed for channel {Id}", ch.Id); }
        }
    }

    public async Task SyncChannelAsync(ChannelConfig channel)
    {
        _logger.LogInformation("YouTube plugin: syncing channel {Name} ({Id})", channel.Name, channel.Id);
        var strmWriter = new StrmWriter(_config.StrmRootPath, _config.UseStreamProxy, _config.StreamProxyPort, _logger);

        try
        {
            var client = new YouTubeApiClient(_config.YouTubeApiKey, _logger);

            ChannelInfo? info;
            try
            {
                info = await client.ResolveChannelAsync(channel.Url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resolve channel failed for {Url}", channel.Url);
                channel.LastSyncAt = DateTime.UtcNow;
                channel.LastSyncStatus = $"error: resolve failed: {ex.Message}";
                SaveChannelConfig(channel);
                return;
            }
            if (info == null)
            {
                channel.LastSyncStatus = "error: channel not found";
                SaveChannelConfig(channel);
                return;
            }

            // Update name if changed
            if (!string.IsNullOrEmpty(info.Title) && info.Title != channel.Name)
            {
                strmWriter.DeleteChannelByName(channel.Name);
                channel.Name = info.Title;
            }

            strmWriter.EnsureChannelDirectory(channel.Name);

            // Fetch latest videos
            var videos = await client.ListChannelVideosAsync(info.UploadsPlaylistId, maxResults: 50).ConfigureAwait(false);
            int added = 0;
            foreach (var v in videos)
            {
                var row = new VideoRow
                {
                    Id = v.Id,
                    ChannelId = channel.Id,
                    Title = v.Title,
                    Description = v.Description,
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
            SaveChannelConfig(channel);

            ApplyRetention(channel, strmWriter);

            _logger.LogInformation("YouTube plugin: sync complete for {Name}: {Count} videos", channel.Name, added);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "YouTube plugin: sync failed for {Name}", channel.Name);
            channel.LastSyncAt = DateTime.UtcNow;
            channel.LastSyncStatus = $"error: {ex.Message}";
            SaveChannelConfig(channel);
        }
    }

    private void SaveChannelConfig(ChannelConfig channel)
    {
        // SQLite mirror of channel row (for join queries with videos/watched)
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
        try { _scheduler?.Shutdown(waitForJobsToComplete: false).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Scheduler shutdown error"); }
    }
}

/// <summary>
/// Quartz job executed per-channel on its polling interval.
/// </summary>
internal class ChannelSyncJob : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var channelId = context.MergedJobDataMap.GetString("channelId");
        if (string.IsNullOrEmpty(channelId)) return Task.CompletedTask;
        return PluginServiceHost.RunChannelSyncAsync(channelId);
    }
}

/// <summary>
/// Static host to allow Quartz jobs to reach the live ChannelSyncService
/// without DI injection complexity (Quartz instantiates jobs parameterless).
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
