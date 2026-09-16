using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Data;
using Jellyfin.Plugin.YouTube.Streaming;
using Jellyfin.Plugin.YouTube.Sync;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube;

/// <summary>
/// Registers the plugin's services in Jellyfin's DI container.
/// This is the modern Jellyfin 10.11 plugin pattern (IPluginServiceRegistrator),
/// replacing the older IServerEntryPoint which no longer exists.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost serverApplicationHost)
    {
        serviceCollection.AddHostedService<PluginHostedService>();
    }
}

/// <summary>
/// Hosted service that lives for the lifetime of Jellyfin. Starts the scheduler,
/// the stream proxy, the watched tracker, and provides access to them via static
/// PluginServiceHost (for Quartz jobs that aren't DI-constructed).
/// </summary>
public class PluginHostedService : IHostedService, IDisposable
{
    private readonly ILogger<PluginHostedService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private PluginConfiguration? _config;
    private SQLiteStore? _db;
    private ChannelSyncService? _syncService;
    private StreamProxy? _streamProxy;
    private WatchedTracker? _watchedTracker;
    private bool _disposed;

    public PluginHostedService(
        ILogger<PluginHostedService> logger,
        ILibraryManager libraryManager,
        ISessionManager sessionManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Plugin YouTube: iniciando servicio... ");

            _config = Plugin.Instance?.Configuration;
            if (_config == null)
            {
                _logger.LogError("Plugin YouTube: Plugin.Instance es null - se cancela la inicialización");
                return Task.CompletedTask;
            }

            // Verificar que yt-dlp esté disponible
            var ytDlpClient = new Api.YtDlpChannelClient(_config.YtDlpPath, _logger);
            var version = ytDlpClient.GetVersion();
            if (version == null)
            {
                _logger.LogError("Plugin YouTube: no se encuentra yt-dlp en '{Path}'. " +
                    "El plugin no funcionará hasta que lo configures correctamente.", _config.YtDlpPath);
            }
            else
            {
                _logger.LogInformation("Plugin YouTube: yt-dlp detectado, versión {Version}", version);
            }

            // Asegurar que existe el directorio raíz de .strm
            if (!System.IO.Directory.Exists(_config.StrmRootPath))
            {
                System.IO.Directory.CreateDirectory(_config.StrmRootPath);
                _logger.LogInformation("Plugin YouTube: directorio raíz creado {Path}", _config.StrmRootPath);
            }

            // Inicializar SQLite
            var dbPath = System.IO.Path.Combine(_config.StrmRootPath, "youtube_plugin.sqlite");
            _db = new SQLiteStore(dbPath, _logger);
            _db.Initialize();
            _logger.LogInformation("Plugin YouTube: SQLite inicializado en {Path}", dbPath);

            // Inicializar watched tracker (suscripto a ISessionManager)
            _watchedTracker = new WatchedTracker(_db, _sessionManager, _logger);
            _watchedTracker.Start();

            // Inicializar stream proxy
            if (_config.UseStreamProxy)
            {
                _streamProxy = new StreamProxy(_config, _db, _logger);
                _ = _streamProxy.StartAsync();
                _logger.LogInformation("Plugin YouTube: proxy de streaming iniciado en el puerto {Port}", _config.StreamProxyPort);
            }

            // Inicializar servicio de sincronización (scheduler)
            _syncService = new ChannelSyncService(_config, _db, _logger);
            PluginServiceHost.SyncService = _syncService;
            PluginServiceHost.Database = _db;
            _ = _syncService.StartAsync();

            _logger.LogInformation("Plugin YouTube: servicio iniciado correctamente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin YouTube: falló la inicialización");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Plugin YouTube: deteniendo servicio...");
        try
        {
            _syncService?.Dispose();
            _streamProxy?.Dispose();
            _watchedTracker?.Dispose();
            _db?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin YouTube: error al detener");
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _syncService?.Dispose();
            _streamProxy?.Dispose();
            _watchedTracker?.Dispose();
            _db?.Dispose();
        }
        catch { /* ignore */ }
    }
}
