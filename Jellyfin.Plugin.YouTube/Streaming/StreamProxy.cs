using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Streaming;

/// <summary>
/// Proxy HTTP local que resuelve URLs de stream de YouTube usando yt-dlp
/// y las redirige hacia Jellyfin.
///
/// Los archivos .strm apuntan a este proxy:
///   http://127.0.0.1:PUERTO/youtube_plugin/stream/VIDEO_ID
///
/// Flujo:
///   1. Jellyfin abre el .strm y hace GET al proxy
///   2. El proxy ejecuta: yt-dlp -g "https://youtu.be/VIDEO_ID"
///   3. yt-dlp devuelve la URL firmada del stream (válido ~6h)
///   4. El proxy responde con HTTP 302 Redirect a esa URL
///   5. Jellyfin reproduce directamente desde YouTube (sin tocar disco)
///
/// Cache de URLs firmadas por StreamUrlCacheHours horas (default 5).
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
                    _logger.LogError(ex, "Error en el listener del proxy de streaming");
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
                await WriteAsync(ctx, "No encontrado");
                return;
            }

            var videoId = path.Substring("/youtube_plugin/stream/".Length).TrimEnd('/');
            if (string.IsNullOrEmpty(videoId))
            {
                ctx.Response.StatusCode = 400;
                await WriteAsync(ctx, "Falta ID de video");
                return;
            }

            var streamUrl = await ResolveStreamUrlAsync(videoId).ConfigureAwait(false);
            if (streamUrl == null)
            {
                ctx.Response.StatusCode = 502;
                await WriteAsync(ctx, "No se pudo resolver la URL del stream de YouTube");
                return;
            }

            _logger.LogInformation("Proxy: redirigiendo {VideoId} -> stream", videoId);

            // Redirección 302: Jellyfin sigue la URL sin pasar bytes por nuestro proceso
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = streamUrl;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error del proxy manejando {Url}", ctx.Request.Url);
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteAsync(ctx, "Error interno");
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Resuelve la URL del stream con cache.
    /// 1. Revisa cache (TTL = StreamUrlCacheHours)
    /// 2. Si no cachedado o expirado, ejecuta yt-dlp -g
    /// </summary>
    public async Task<string?> ResolveStreamUrlAsync(string videoId)
    {
        // 1. Cache hit
        if (_cache.TryGetValue(videoId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            _logger.LogDebug("Cache hit para {VideoId}", videoId);
            return cached.Url;
        }

        // 2. Resolver con yt-dlp
        string? resolvedUrl = null;
        try
        {
            resolvedUrl = await ResolveWithYtDlpAsync(videoId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp falló resolviendo {VideoId}", videoId);
        }

        if (resolvedUrl == null)
        {
            _logger.LogError("Resolución fallida para el video {Id}", videoId);
            return null;
        }

        // 3. Guardar en cache
        var cacheHours = Math.Max(1, _config.StreamUrlCacheHours);
        _cache[videoId] = new CachedStreamUrl
        {
            Url = resolvedUrl,
            ExpiresAt = DateTime.UtcNow.AddHours(cacheHours)
        };

        return resolvedUrl;
    }

    /// <summary>
    /// Resuelve la URL del stream vía yt-dlp.
    /// Maneja: videos normales, age-restricted (con cookies), live streams.
    /// </summary>
    private async Task<string?> ResolveWithYtDlpAsync(string videoId)
    {
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrEmpty(_config.YtDlpPath) ? "yt-dlp" : _config.YtDlpPath,
            Arguments = $"-g -f \"best[height<={_config.MaxQuality}]/best\" " +
                        "--no-warnings --no-playlist " +
                        $"\"https://www.youtube.com/watch?v={videoId}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var exited = proc.WaitForExit(30000);
        if (!exited)
        {
            try { proc.Kill(); } catch { /* ignore */ }
            _logger.LogWarning("yt-dlp timeout para el video {Id}", videoId);
            return null;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var stdout = stdoutTask.Result.Trim();
        var stderr = stderrTask.Result.Trim();

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning("yt-dlp falló para {Id}: exit={Code} err={Err}",
                videoId, proc.ExitCode, stderr);
            return null;
        }

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        return lines[0].Trim();
    }

    private static async Task WriteAsync(HttpListenerContext ctx, string msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg);
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
