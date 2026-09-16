using System;
using System.IO;
using Jellyfin.Plugin.YouTube.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Sync;

/// <summary>
/// Writes .strm files for each video. The .strm contains:
///   http://127.0.0.1:PORT/youtube_plugin/stream/VIDEO_ID
///
/// Directory structure under StrmRootPath:
///   {StrmRootPath}/{channel_name}/Season 01/{video_id}.strm
/// </summary>
public class StrmWriter
{
    private readonly string _rootPath;
    private readonly bool _useProxy;
    private readonly int _proxyPort;
    private readonly ILogger _logger;

    public StrmWriter(string rootPath, bool useProxy, int proxyPort, ILogger logger)
    {
        _rootPath = rootPath;
        _useProxy = useProxy;
        _proxyPort = proxyPort;
        _logger = logger;
    }

    public void EnsureChannelDirectory(string channelName)
    {
        var safeName = SafeName(channelName);
        var channelDir = Path.Combine(_rootPath, safeName);
        var seasonDir = Path.Combine(channelDir, "Season 01");
        Directory.CreateDirectory(seasonDir);
    }

    public void WriteStrm(ChannelConfig channel, Data.VideoRow video)
    {
        var safeChannel = SafeName(channel.Name);
        var seasonDir = Path.Combine(_rootPath, safeChannel, "Season 01");
        Directory.CreateDirectory(seasonDir);

        var strmPath = Path.Combine(seasonDir, $"{video.Id}.strm");
        var content = _useProxy
            ? $"http://127.0.0.1:{_proxyPort}/youtube_plugin/stream/{video.Id}"
            : $"https://www.youtube.com/watch?v={video.Id}";

        File.WriteAllText(strmPath, content);
    }

    public void DeleteStrm(string channelName, string videoId)
    {
        var safeChannel = SafeName(channelName);
        var strmPath = Path.Combine(_rootPath, safeChannel, "Season 01", $"{videoId}.strm");
        if (File.Exists(strmPath))
        {
            try { File.Delete(strmPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete {Path}", strmPath); }
        }
    }

    public void DeleteChannelByName(string channelName)
    {
        var safeChannel = SafeName(channelName);
        var channelDir = Path.Combine(_rootPath, safeChannel);
        if (Directory.Exists(channelDir))
        {
            try { Directory.Delete(channelDir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete {Path}", channelDir); }
        }
    }

    public void DeleteChannel(ChannelConfig channel)
        => DeleteChannelByName(channel.Name);

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = name.ToCharArray();
        for (int i = 0; i < safe.Length; i++)
        {
            if (Array.IndexOf(invalid, safe[i]) >= 0) safe[i] = '_';
        }
        var result = new string(safe).Trim();
        if (string.IsNullOrEmpty(result)) result = "Unknown";
        if (result.Length > 80) result = result.Substring(0, 80);
        return result;
    }
}
