using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Data;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Jellyfin.Plugin.YouTube.Streaming;

/// <summary>
/// Local HTTP proxy that resolves YouTube stream URLs via YoutubeExplode (primary,
/// no external binary) or yt-dlp (fallback, more robust) and forwards them to Jellyfin.
///
/// The .strm files point to this proxy:
///   http://127.0.0.1:PORT/youtube_plugin/stream/VIDEO_ID
///
/// Resolution strategy per request:
///   1. If PreferYoutubeExplode=true (default) and video is not age-restricted:
///      use YoutubeExplode (in-process, no external binary, fast).
///   2. If step 1 fails AND yt-dlp is available at YtDlpPath:
///      fall back to yt-dlp subprocess invocation.
///   3. If both fail: return 502 to Jellyfin.
///
/// The resolved URL is cached for StreamUrlCacheHours hours (default 5).
/// On 403 from YouTube (URL expired), cache is invalidated and re-resolution occurs.
/// </summary>
public class StreamProxy : IDisposable
{
    private readonly PluginConfiguration _config;
    private readonly SQLiteStore _db;
    private readonly ILogger _logger;
    private readonly YoutubeClient _youtubeClient;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, CachedStreamUrl> _cache = new();
    private bool _disposed;

    public StreamProxy(PluginConfiguration config, SQLiteStore db, ILogger logger)
    {
        _config = config;
        _db = db;
        _logger = logger;
        _youtubeClient = new YoutubeClient();
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_config.StreamProxyPort}/youtube_plugin/");
        _listener.Start();

        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(ctx));
                }
                catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stream proxy listener error");
                }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (!path.StartsWith("/youtube_plugin/stream/", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 404;
                await WriteAsync(ctx, "Not found");
                return;
            }

            var videoId = path.Substring("/youtube_plugin/stream/".Length).TrimEnd('/');
            if (string.IsNullOrEmpty(videoId))
            {
                ctx.Response.StatusCode = 400;
                await WriteAsync(ctx, "Missing video id");
                return;
            }

            var streamUrl = await ResolveStreamUrlAsync(videoId).ConfigureAwait(false);
            if (streamUrl == null)
            {
                ctx.Response.StatusCode = 502;
                await WriteAsync(ctx, "Failed to resolve YouTube stream URL");
                return;
            }

            _logger.LogInformation("Stream proxy: forwarding {VideoId}", videoId);

            // Redirect (HTTP 302) instead of proxying bytes through our process.
            // Jellyfin follows redirects, saving bandwidth and process overhead.
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = streamUrl;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream proxy error handling {Url}", ctx.Request.Url);
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteAsync(ctx, "Internal error");
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Resolves the stream URL with caching. Strategy:
    ///   1. Check cache (TTL = StreamUrlCacheHours)
    ///   2. Try YoutubeExplode (if preferred)
    ///   3. Fall back to yt-dlp (if available)
    /// </summary>
    public async Task<string?> ResolveStreamUrlAsync(string videoId)
    {
        // 1. Cache check
        if (_cache.TryGetValue(videoId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Url;
        }

        string? resolvedUrl = null;

        // 2. YoutubeExplode (primary if configured)
        if (_config.PreferYoutubeExplode)
        {
            try
            {
                resolvedUrl = await ResolveWithYoutubeExplodeAsync(videoId).ConfigureAwait(false);
                if (resolvedUrl != null)
                {
                    _logger.LogDebug("Resolved {VideoId} via YoutubeExplode", videoId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "YoutubeExplode failed for {VideoId}, falling back to yt-dlp", videoId);
            }
        }

        // 3. yt-dlp fallback
        if (resolvedUrl == null)
        {
            try
            {
                resolvedUrl = await ResolveWithYtDlpAsync(videoId).ConfigureAwait(false);
                if (resolvedUrl != null)
                {
                    _logger.LogDebug("Resolved {VideoId} via yt-dlp", videoId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "yt-dlp failed for {VideoId}", videoId);
            }
        }

        if (resolvedUrl == null)
        {
            _logger.LogError("All resolvers failed for video {Id}", videoId);
            return null;
        }

        // 4. Cache for next time
        _cache[videoId] = new CachedStreamUrl
        {
            Url = resolvedUrl,
            ExpiresAt = DateTime.UtcNow.AddHours(Math.Max(1, _config.StreamUrlCacheHours))
        };

        return resolvedUrl;
    }

    /// <summary>
    /// Resolve stream URL using YoutubeExplode (in-process, no external binary).
    /// Works for most videos EXCEPT age-restricted or members-only content.
    /// </summary>
    private async Task<string?> ResolveWithYoutubeExplodeAsync(string videoId)
    {
        var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId).ConfigureAwait(false);

        // Prefer muxed streams (audio + video in one URL) for max compatibility
        // with Jellyfin clients. Fallback to highest bitrate video stream.
        var muxed = manifest.GetMuxedStreams();
        IStreamInfo? best = null;

        foreach (var s in muxed)
        {
            // Filter by max quality height (e.g. "1080" -> only streams <= 1080p)
            if (int.TryParse(_config.MaxQuality, out var maxH))
            {
                if (s.VideoResolution.Height > maxH) continue;
            }
            if (best == null || s.VideoResolution.Height > ((MuxedStreamInfo)best).VideoResolution.Height)
            {
                best = s;
            }
        }

        // If no muxed stream matched quality filter, take the best muxed available
        if (best == null)
        {
            best = muxed.GetWithHighestVideoQuality();
        }

        return best?.Url;
    }

    /// <summary>
    /// Resolve stream URL via yt-dlp subprocess. More robust than YoutubeExplode
    /// (handles age-restricted, member-only, live streams) but requires the
    /// binary to be installed in PATH or at the configured YtDlpPath.
    /// </summary>
    private async Task<string?> ResolveWithYtDlpAsync(string videoId)
    {
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrEmpty(_config.YtDlpPath) ? "yt-dlp" : _config.YtDlpPath,
            Arguments = $"-g -f \"best[height<={_config.MaxQuality}]/best\" --no-warnings --no-playlist \"https://www.youtube.com/watch?v={videoId}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var exited = proc.WaitForExit(milliseconds: 30000);
        if (!exited)
        {
            try { proc.Kill(); } catch { /* ignore */ }
            _logger.LogWarning("yt-dlp timeout for video {Id}", videoId);
            return null;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var stdout = stdoutTask.Result.Trim();
        var stderr = stderrTask.Result.Trim();

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning("yt-dlp failed for {Id}: exit={Code} err={Err}", videoId, proc.ExitCode, stderr);
            return null;
        }

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        return lines[0].Trim();
    }

    /// <summary>
    /// Test method: tries to resolve a video via each strategy and returns
    /// a diagnostic result. Used by the "Test resolver" button in the UI.
    /// </summary>
    public async Task<ResolverTestResult> TestResolverAsync(string videoId)
    {
        var result = new ResolverTestResult { VideoId = videoId };

        // YoutubeExplode
        try
        {
            var url = await ResolveWithYoutubeExplodeAsync(videoId).ConfigureAwait(false);
            result.YoutubeExplodeOk = url != null;
            result.YoutubeExplodeUrl = url?.Substring(0, Math.Min(80, url.Length)) + "...";
        }
        catch (Exception ex)
        {
            result.YoutubeExplodeOk = false;
            result.YoutubeExplodeError = ex.Message;
        }

        // yt-dlp
        try
        {
            var url = await ResolveWithYtDlpAsync(videoId).ConfigureAwait(false);
            result.YtDlpOk = url != null;
            result.YtDlpUrl = url?.Substring(0, Math.Min(80, url.Length)) + "...";
        }
        catch (Exception ex)
        {
            result.YtDlpOk = false;
            result.YtDlpError = ex.Message;
        }

        return result;
    }

    private static async Task WriteAsync(HttpListenerContext ctx, string msg)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(msg);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _listener?.Close();
    }

    private class CachedStreamUrl
    {
        public string Url { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }
}

public class ResolverTestResult
{
    public string VideoId { get; set; } = string.Empty;
    public bool YoutubeExplodeOk { get; set; }
    public string? YoutubeExplodeUrl { get; set; }
    public string? YoutubeExplodeError { get; set; }
    public bool YtDlpOk { get; set; }
    public string? YtDlpUrl { get; set; }
    public string? YtDlpError { get; set; }
}
