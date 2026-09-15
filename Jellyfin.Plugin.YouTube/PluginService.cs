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
    private PluginConfiguration? _config;
    private SQLiteStore? _db;
    private ChannelSyncService? _syncService;
    private StreamProxy? _streamProxy;
    private WatchedTracker? _watchedTracker;
    private bool _disposed;

    public PluginHostedService(
        ILogger<PluginHostedService> logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("YouTube plugin: starting hosted service...");

            _config = Plugin.Instance?.Configuration;
            if (_config == null)
            {
                _logger.LogError("YouTube plugin: Plugin.Instance is null - aborting init");
                return Task.CompletedTask;
            }

            // Ensure strm root directory exists
            if (!System.IO.Directory.Exists(_config.StrmRootPath))
            {
                System.IO.Directory.CreateDirectory(_config.StrmRootPath);
                _logger.LogInformation("YouTube plugin: created strm root {Path}", _config.StrmRootPath);
            }

            // Initialize SQLite
            var dbPath = System.IO.Path.Combine(_config.StrmRootPath, "youtube_plugin.sqlite");
            _db = new SQLiteStore(dbPath, _logger);
            _db.Initialize();
            _logger.LogInformation("YouTube plugin: SQLite initialized at {Path}", dbPath);

            // Initialize watched tracker
            _watchedTracker = new WatchedTracker(_db, _logger);
            _watchedTracker.Start();

            // Initialize stream proxy
            if (_config.UseStreamProxy)
            {
                _streamProxy = new StreamProxy(_config, _db, _logger);
                _ = _streamProxy.StartAsync();
                _logger.LogInformation("YouTube plugin: stream proxy started on port {Port}", _config.StreamProxyPort);
            }

            // Initialize channel sync service (scheduler)
            _syncService = new ChannelSyncService(_config, _db, _logger);
            PluginServiceHost.SyncService = _syncService;
            PluginServiceHost.Database = _db;
            _ = _syncService.StartAsync();

            _logger.LogInformation("YouTube plugin: hosted service started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "YouTube plugin: initialization failed");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("YouTube plugin: stopping hosted service...");
        try
        {
            _syncService?.Dispose();
            _streamProxy?.Dispose();
            _watchedTracker?.Dispose();
            _db?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube plugin: error during stop");
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
