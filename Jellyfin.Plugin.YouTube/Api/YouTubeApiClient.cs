using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

/// <summary>
/// Wraps YouTube Data API v3. Quota consumption:
///   - search by channel: 100 units (we avoid this; use channel->playlistID then playlistItems)
///   - channels.list: 1 unit per 50 channels
///   - playlistItems.list: 1 unit per 50 items
///   - videos.list: 1 unit per 50 videos
/// Recommended flow: resolve channel uploads playlist once (1 unit), then poll playlistItems (1 unit per 50 videos).
/// </summary>
public class YouTubeApiClient
{
    private readonly string _apiKey;
    private readonly ILogger _logger;
    private readonly YouTubeService _service;

    public YouTubeApiClient(string apiKey, ILogger logger)
    {
        _apiKey = apiKey;
        _logger = logger;
        _service = new YouTubeService(new BaseClientService.Initializer
        {
            ApiKey = apiKey,
            ApplicationName = "JellyfinYouTubePlugin"
        });
    }

    /// <summary>
    /// Resolves a YouTube URL (channel/handle/video) to a Channel resource.
    /// Accepted inputs:
    ///   - https://www.youtube.com/channel/UCxxxx
    ///   - https://www.youtube.com/@handle
    ///   - https://www.youtube.com/c/CustomName
    ///   - https://www.youtube.com/user/UserName
    ///   - https://www.youtube.com/watch?v=VIDEO_ID (resolves to channel)
    /// </summary>
    public async Task<ChannelInfo?> ResolveChannelAsync(string urlOrId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("YouTube API key is not configured");

        ChannelsResource.ListRequest? req = null;
        string? channelId = null;

        if (urlOrId.Contains("youtube.com"))
        {
            if (urlOrId.Contains("/channel/"))
            {
                channelId = ExtractAfter(urlOrId, "/channel/");
                channelId = channelId.Split('/')[0];
            }
            else if (urlOrId.Contains("/@"))
            {
                var handle = ExtractAfter(urlOrId, "/@");
                handle = handle.Split('/')[0].Split('?')[0];
                req = _service.Channels.List("snippet,contentDetails");
                req.ForHandle = "@" + handle;
            }
            else if (urlOrId.Contains("/user/"))
            {
                var userName = ExtractAfter(urlOrId, "/user/");
                userName = userName.Split('/')[0].Split('?')[0];
                req = _service.Channels.List("snippet,contentDetails");
                req.ForUsername = userName;
            }
            else if (urlOrId.Contains("/c/"))
            {
                // /c/ custom name - not directly resolvable via API. Fall back to search.
                var customName = ExtractAfter(urlOrId, "/c/");
                customName = customName.Split('/')[0].Split('?')[0];
                var searchReq = _service.Search.List("snippet");
                searchReq.Q = customName;
                searchReq.Type = "channel";
                searchReq.MaxResults = 1;
                var searchResp = await searchReq.ExecuteAsync(ct).ConfigureAwait(false);
                if (searchResp.Items.Count == 0) return null;
                channelId = searchResp.Items[0].Id.ChannelId;
            }
            else if (urlOrId.Contains("watch?v="))
            {
                var videoId = ExtractQueryParam(urlOrId, "v");
                if (string.IsNullOrEmpty(videoId)) return null;
                var vReq = _service.Videos.List("snippet");
                vReq.Id = videoId;
                var vResp = await vReq.ExecuteAsync(ct).ConfigureAwait(false);
                if (vResp.Items.Count == 0) return null;
                channelId = vResp.Items[0].Snippet.ChannelId;
            }
        }
        else
        {
            // Assume raw channel ID
            channelId = urlOrId.Trim();
        }

        if (req == null && !string.IsNullOrEmpty(channelId))
        {
            req = _service.Channels.List("snippet,contentDetails");
            req.Id = channelId;
        }
        if (req == null) return null;

        var resp2 = await req.ExecuteAsync(ct).ConfigureAwait(false);
        if (resp2.Items.Count == 0) return null;

        var ch = resp2.Items[0];
        var uploadsPlaylistId = ch.ContentDetails?.RelatedPlaylists?.Uploads ?? "";
        var thumbnail = ch.Snippet?.Thumbnails?.High?.Url
                        ?? ch.Snippet?.Thumbnails?.Medium?.Url
                        ?? ch.Snippet?.Thumbnails?.Default__.Url
                        ?? "";
        return new ChannelInfo
        {
            Id = ch.Id,
            Title = ch.Snippet?.Title ?? ch.Id,
            Description = ch.Snippet?.Description ?? "",
            UploadsPlaylistId = uploadsPlaylistId,
            ThumbnailUrl = thumbnail
        };
    }

    /// <summary>
    /// Lists the most recent videos uploaded to a channel.
    /// Returns up to maxResults items (max 50 per API call; uses pagination if more).
    /// </summary>
    public async Task<List<VideoInfo>> ListChannelVideosAsync(
        string uploadsPlaylistId, int maxResults = 50, CancellationToken ct = default)
    {
        var result = new List<VideoInfo>();
        string? pageToken = null;
        var remaining = maxResults;

        do
        {
            var req = _service.PlaylistItems.List("snippet,contentDetails");
            req.PlaylistId = uploadsPlaylistId;
            req.MaxResults = Math.Min(50, remaining);
            if (pageToken != null) req.PageToken = pageToken;

            var resp = await req.ExecuteAsync(ct).ConfigureAwait(false);
            var videoIds = resp.Items.Select(i => i.ContentDetails.VideoId).Where(v => !string.IsNullOrEmpty(v)).ToList();
            if (videoIds.Count == 0) break;

            // Fetch video details (duration, snippet) in one call
            var vReq = _service.Videos.List("snippet,contentDetails");
            vReq.Id = string.Join(",", videoIds);
            var vResp = await vReq.ExecuteAsync(ct).ConfigureAwait(false);

            foreach (var v in vResp.Items)
            {
                var thumb = v.Snippet?.Thumbnails?.High?.Url
                            ?? v.Snippet?.Thumbnails?.Medium?.Url
                            ?? v.Snippet?.Thumbnails?.Default__.Url
                            ?? "";
                var duration = ParseISODuration(v.ContentDetails?.Duration ?? "PT0S");
                result.Add(new VideoInfo
                {
                    Id = v.Id,
                    Title = v.Snippet?.Title ?? v.Id,
                    Description = v.Snippet?.Description ?? "",
                    PublishedAt = DateTime.Parse(v.Snippet?.PublishedAtRaw ?? DateTime.UtcNow.ToString("O")),
                    DurationSeconds = duration,
                    ThumbnailUrl = thumb,
                    ChannelId = v.Snippet?.ChannelId ?? ""
                });
                remaining--;
                if (remaining <= 0) break;
            }

            pageToken = resp.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken) && remaining > 0);

        return result;
    }

    private static string? ExtractAfter(string s, string marker)
    {
        var idx = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : s.Substring(idx + marker.Length);
    }

    private static string? ExtractQueryParam(string url, string param)
    {
        var uri = new Uri(url);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return q[param];
    }

    private static int ParseISODuration(string iso)
    {
        // Format: PT#H#M#S e.g. PT1H2M3S, PT5M30S, PT45S
        if (string.IsNullOrEmpty(iso)) return 0;
        var s = iso.Substring(2); // strip "PT"
        int total = 0, num = 0;
        foreach (var c in s)
        {
            if (char.IsDigit(c))
            {
                num = num * 10 + (c - '0');
            }
            else
            {
                total = c switch
                {
                    'H' => total + num * 3600,
                    'M' => total + num * 60,
                    'S' => total + num,
                    _ => total
                };
                num = 0;
            }
        }
        return total;
    }
}

public class ChannelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string UploadsPlaylistId { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
}

public class VideoInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
}
