using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

/// <summary>
/// Channel metadata and video listing via yt-dlp --flat-playlist.
///
/// No YouTube Data API v3 key required. No quota. No external NuGet deps.
///
/// yt-dlp --flat-playlist prints one line per video with templated fields:
///   yt-dlp --flat-playlist --print "%(id)s|%(title)s|%(duration)s|%(upload_date)s|%(thumbnail)s" URL
///
/// Returns ~50-200 videos per channel in a single invocation (~1-3s).
/// </summary>
public class YtDlpChannelClient
{
    private readonly string _ytDlpPath;
    private readonly ILogger _logger;

    public YtDlpChannelClient(string ytDlpPath, ILogger logger)
    {
        _ytDlpPath = string.IsNullOrEmpty(ytDlpPath) ? "yt-dlp" : ytDlpPath;
        _logger = logger;
    }

    /// <summary>
    /// Resolves channel info from a URL. yt-dlp handles all URL formats:
    ///   /@handle, /channel/UCxxxx, /c/CustomName, /user/Name, /watch?v=...
    /// Returns the canonical channel URL, name, and ID.
    /// </summary>
    public ChannelInfo? ResolveChannel(string url)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = $"--no-warnings --no-playlist --print \"%(channel_id)s|%(channel)s|%(uploader_url)s\" \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("yt-dlp resolve channel failed: {Err}", stderr.Trim());
                return null;
            }

            var line = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
            var parts = line.Split('|');
            if (parts.Length < 3) return null;

            var channelId = parts[0];
            var channelName = parts[1];
            var channelUrl = parts[2];

            // For listing uploads, use the /videos URL
            var uploadsUrl = channelUrl.EndsWith("/videos")
                ? channelUrl
                : channelUrl.TrimEnd('/') + "/videos";

            return new ChannelInfo
            {
                Id = channelId,
                Title = channelName,
                Url = channelUrl,
                UploadsUrl = uploadsUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp invocation failed resolving channel {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Lists the most recent videos uploaded to a channel via yt-dlp --flat-playlist.
    /// Returns up to maxResults items (default 50). Each item has: id, title,
    /// durationSeconds, publishedAt, thumbnailUrl.
    /// </summary>
    public List<VideoInfo> ListChannelVideos(string uploadsUrl, int maxResults = 50)
    {
        var result = new List<VideoInfo>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                // --flat-playlist: don't download, just list
                // --print with pipe-delimited fields
                // --playlist-end: limit to maxResults
                Arguments = $"--flat-playlist --no-warnings --playlist-end {maxResults} " +
                            "--print \"%(id)s|%(title)s|%(duration)s|%(upload_date)s|%(thumbnail)s\" " +
                            $"\"{uploadsUrl}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return result;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("yt-dlp list videos failed: {Err}", stderr.Trim());
                return result;
            }

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length < 5) continue;

                var videoId = parts[0].Trim();
                var title = parts[1].Trim();
                var durationStr = parts[2].Trim();
                var dateStr = parts[3].Trim();
                var thumb = parts[4].Trim();

                if (string.IsNullOrEmpty(videoId)) continue;

                int duration = 0;
                if (int.TryParse(durationStr, out var d)) duration = d;

                DateTime publishedAt = DateTime.UtcNow;
                if (dateStr.Length == 8 &&
                    DateTime.TryParseExact(dateStr, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                {
                    publishedAt = dt;
                }

                result.Add(new VideoInfo
                {
                    Id = videoId,
                    Title = title,
                    DurationSeconds = duration,
                    PublishedAt = publishedAt,
                    ThumbnailUrl = thumb,
                    ChannelId = ""
                });

                if (result.Count >= maxResults) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp invocation failed listing videos for {Url}", uploadsUrl);
        }
        return result;
    }

    /// <summary>
    /// Tests if yt-dlp is installed and returns its version string.
    /// </summary>
    public string? GetVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp version check failed");
            return null;
        }
    }
}

public class ChannelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string UploadsUrl { get; set; } = string.Empty;
}

public class VideoInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public DateTime PublishedAt { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
}
