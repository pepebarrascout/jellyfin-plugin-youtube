using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube;

/// <summary>
/// Main plugin entry point. Assembly loaded by Jellyfin at startup.
/// Service registration happens in PluginServiceRegistrator.cs.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        Instance = this;
        _logger.LogInformation("YouTube Streaming plugin v{Version} loaded", Version);
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "YouTube Streaming";

    public override string Description =>
        "Stream YouTube videos as Jellyfin Series without downloading them.";

    public override Guid Id => Guid.Parse("a8c3b2e1-7f4d-4e6a-9b1c-2d5e8f0a1b3c");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "YouTubePlugin",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configurationpage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                DisplayName = "YouTube Streaming",
                MenuIcon = "youtube"
            },
            new PluginPageInfo
            {
                Name = "YouTubePluginJS",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configurationpage.js"
            }
        };
    }
}
