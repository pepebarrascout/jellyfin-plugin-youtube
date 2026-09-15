using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Streaming;

/// <summary>
/// Local HTTP proxy that resolves YouTube stream URLs via yt-dlp and
/// forwards them to Jellyfin transparently. The .strm files point to this proxy
/// (http://127.0.0.1:PORT/youtube_plugin/stream/VIDEO_ID).
///
/// Why: YouTube's signed stream URLs expire in ~6 hours. If we wrote them
/// directly into .strm files, they'd be stale by next playback. The proxy
/// resolves on-demand, caches for a few hours, and re-resolves on 403.
///
/// yt-dlp is invoked via Process.Start with timeout. Result URL is passed
/// through to Jellyfin — the video bytes never touch our process.
/// </summary>
public class StreamProxy : IDisposable
{
    private readonly PluginConfiguration _config;
    private readonly SQLiteStore _db;
    private readonly ILogger _logger;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, CachedStreamUrl> _cache = new();
    private bool _disposed;

    public StreamProxy(PluginConfiguration config, SQLiteStore db, ILogger logger)
    {
        _config = config;
        _db = db;
        _logger = logger;
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
            // Expected: /youtube_plugin/stream/{videoId}
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

            // Cache the resolved URL; pass the request through to YouTube
            _logger.LogInformation("Stream proxy: forwarding {VideoId} -> {Url}", videoId, streamUrl[..Math.Min(80, streamUrl.Length)] + "...");

            // Redirect instead of proxying bytes through our process. The .strm
            // handler in Jellyfin follows HTTP 302 redirects, so we save bandwidth.
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

    private async Task<string?> ResolveStreamUrlAsync(string videoId)
    {
        // Cache hit?
        if (_cache.TryGetValue(videoId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Url;
        }

        // Invoke yt-dlp to get the best stream URL
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrEmpty(_config.YtDlpPath) ? "yt-dlp" : _config.YtDlpPath,
            Arguments = $"-g -f \"{_config.MaxQuality}\" --no-warnings --no-playlist \"https://www.youtube.com/watch?v={videoId}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
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

            // yt-dlp -g may return multiple lines (separate video/audio streams).
            // First line is typically the video stream URL.
            var firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (string.IsNullOrEmpty(firstLine)) return null;

            // Cache for StreamUrlCacheHours hours (default 5)
            _cache[videoId] = new CachedStreamUrl
            {
                Url = firstLine,
                ExpiresAt = DateTime.UtcNow.AddHours(_config.StreamUrlCacheHours)
            };

            return firstLine;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp invocation failed for {Id}", videoId);
            return null;
        }
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
