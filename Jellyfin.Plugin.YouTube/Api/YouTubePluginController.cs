using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

/// <summary>
/// Placeholder for the plugin's internal API controller.
/// NOTE: Jellyfin 10.11 uses ASP.NET Core controllers derived from
/// BaseFrontendController. In this v0.0.0.1 scaffold we expose only the
/// configuration page (via IHasWebPages). Full REST endpoints will be
/// wired up in the next iteration using Jellyfin's controller pattern.
///
/// The configuration page currently calls these endpoints via fetch():
///   GET    /YouTubePlugin/channels
///   POST   /YouTubePlugin/channels
///   DELETE /YouTubePlugin/channels/{id}
///   POST   /YouTubePlugin/channels/{id}/sync
///   GET    /YouTubePlugin/status
///   POST   /YouTubePlugin/test-ytdlp
///
/// In v0.0.0.2 we'll implement these as proper Jellyfin controllers.
/// </summary>
public static class YouTubePluginApiSpec
{
    public const string RoutePrefix = "/YouTubePlugin";
    public const string ChannelRoute = "/channels";
    public const string StatusRoute = "/status";
    public const string TestYtDlpRoute = "/test-ytdlp";

    public static string Describe()
        => "REST API endpoints (planned for v0.0.0.2): " +
           "GET/POST /YouTubePlugin/channels, " +
           "GET /YouTubePlugin/status, " +
           "POST /YouTubePlugin/test-ytdlp";
}
