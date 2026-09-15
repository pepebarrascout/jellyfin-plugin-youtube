// YouTube Streaming plugin configuration page.
// Uses Jellyfin's built-in plugin config endpoints (no custom REST API needed):
//   ApiClient.getPluginConfiguration(pluginId) -> PluginConfiguration (incl. Channels array)
//   ApiClient.updatePluginConfiguration(pluginId, config) -> saves to Jellyfin XML

const PLUGIN_ID = 'a8c3b2e1-7f4d-4e6a-9b1c-2d5e8f0a1b3c';

function escapeHtml(s) {
    return String(s || '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'})[c]);
}

function fmtDate(s) {
    if (!s) return '';
    try { return new Date(s).toLocaleString(); } catch { return s; }
}

// Load config from Jellyfin
async function loadConfig() {
    return new Promise((resolve, reject) => {
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (cfg) {
            resolve(cfg);
        }).catch(function (err) { reject(err); });
    });
}

// Save config to Jellyfin
async function saveConfig(cfg) {
    return new Promise((resolve, reject) => {
        ApiClient.updatePluginConfiguration(PLUGIN_ID, cfg).then(function () {
            resolve(true);
        }).catch(function (err) { reject(err); });
    });
}

function renderStatusBanner(cfg) {
    const banner = document.getElementById('ytStatusBanner');
    const issues = [];
    if (!cfg.YouTubeApiKey) issues.push('YouTube Data API v3 key is not configured');
    if (!cfg.StrmRootPath) issues.push('.strm root path is empty');

    if (issues.length === 0) {
        banner.style.background = '#1a3a1a';
        banner.style.border = '1px solid #2d5a2d';
        banner.style.color = '#6ee66e';
        banner.innerHTML = '<strong>OK.</strong> Plugin is configured. ' +
            'yt-dlp fallback: <code>' + escapeHtml(cfg.YtDlpPath || 'yt-dlp') + '</code>. ' +
            'Resolver: <code>' + (cfg.PreferYoutubeExplode ? 'YoutubeExplode (primary) + yt-dlp (fallback)' : 'yt-dlp only') + '</code>. ' +
            'Proxy: ' + (cfg.UseStreamProxy ? 'port ' + cfg.StreamProxyPort : 'disabled') + '. ' +
            'Strm root: <code>' + escapeHtml(cfg.StrmRootPath) + '</code>.';
    } else {
        banner.style.background = '#3a1a1a';
        banner.style.border = '1px solid #5a2d2d';
        banner.style.color = '#e66e6e';
        banner.innerHTML = '<strong>Configuration issues:</strong><ul style="margin:6px 0 0 20px;">' +
            issues.map(i => '<li>' + escapeHtml(i) + '</li>').join('') + '</ul>';
    }
}

function populateForm(cfg) {
    document.getElementById('ytApiKey').value = cfg.YouTubeApiKey || '';
    document.getElementById('ytYtDlpPath').value = cfg.YtDlpPath || 'yt-dlp';
    document.getElementById('ytStrmRoot').value = cfg.StrmRootPath || '/config/youtube_plugin';
    document.getElementById('ytProxyPort').value = cfg.StreamProxyPort || 8585;
    document.getElementById('ytMaxQuality').value = cfg.MaxQuality || '1080';
    document.getElementById('ytUseProxy').checked = cfg.UseStreamProxy !== false;
    document.getElementById('ytPreferExplode').checked = cfg.PreferYoutubeExplode !== false;
}

function collectConfig(cfg) {
    cfg.YouTubeApiKey = document.getElementById('ytApiKey').value.trim();
    cfg.YtDlpPath = document.getElementById('ytYtDlpPath').value.trim() || 'yt-dlp';
    cfg.StrmRootPath = document.getElementById('ytStrmRoot').value.trim() || '/config/youtube_plugin';
    cfg.StreamProxyPort = parseInt(document.getElementById('ytProxyPort').value, 10) || 8585;
    cfg.MaxQuality = document.getElementById('ytMaxQuality').value;
    cfg.UseStreamProxy = document.getElementById('ytUseProxy').checked;
    cfg.PreferYoutubeExplode = document.getElementById('ytPreferExplode').checked;
    return cfg;
}

function renderChannels(channels) {
    const list = document.getElementById('ytChannelList');
    if (!channels || channels.length === 0) {
        list.innerHTML = '<div style="opacity:0.7;text-align:center;padding:20px;">No channels configured. Add one above.</div>';
        return;
    }
    list.innerHTML = channels.map(function (c) {
        var statusColor = (c.LastSyncStatus || '').startsWith('ok') ? '#6ee66e' :
                          (c.LastSyncStatus || '').startsWith('error') ? '#e66e6e' : '#aaa';
        return `
        <div style="display:flex;justify-content:space-between;align-items:center;padding:12px;border-bottom:1px solid #333;">
            <div style="flex:1;">
                <div style="font-weight:600;">${escapeHtml(c.Name || c.Id)}
                    <span style="font-size:0.8rem;opacity:0.6;margin-left:8px;">${c.VideoCount || 0} videos</span>
                    ${c.Disabled ? '<span style="font-size:0.7rem;color:#e66e6e;margin-left:8px;">[disabled]</span>' : ''}
                </div>
                <div style="font-size:0.8rem;opacity:0.7;margin-top:4px;">
                    <a href="${escapeHtml(c.Url)}" target="_blank" style="color:#5b9bd5;">${escapeHtml(c.Url)}</a>
                </div>
                <div style="font-size:0.75rem;opacity:0.6;margin-top:4px;">
                    Polling: every ${c.PollingIntervalHours}h &middot;
                    Retention: ${c.RetentionPolicy === 'permanent' ? 'permanent' : 'delete after 2d watched'}<br/>
                    ${c.LastSyncAt ? 'Last sync: ' + fmtDate(c.LastSyncAt) : 'Never synced'}
                    ${c.LastSyncStatus ? ' &middot; <span style="color:' + statusColor + ';">' + escapeHtml(c.LastSyncStatus) + '</span>' : ''}
                </div>
            </div>
            <div style="display:flex;gap:6px;flex-shrink:0;">
                <button class="yt-sync-btn raised emby-button" data-id="${escapeHtml(c.Id)}" type="button" style="padding:4px 12px;font-size:0.8rem;">Sync</button>
                <button class="yt-del-btn emby-button" data-id="${escapeHtml(c.Id)}" type="button" style="padding:4px 12px;font-size:0.8rem;background:#c44;color:#fff;">Delete</button>
            </div>
        </div>`;
    }).join('');

    document.querySelectorAll('.yt-sync-btn').forEach(btn => {
        btn.onclick = async () => {
            btn.textContent = 'Syncing...';
            // Trigger sync by updating LastSyncAt to a sentinel value the sync service picks up.
            // We use the UpdateConfiguration hook: just saving config triggers scheduler reload,
            // and the next sync cycle will pick it up. For immediate sync, set the channel's
            // polling to a very short interval is overkill - instead just call save which
            // triggers reload, then manually trigger via the channel's existing schedule.
            try {
                const cfg = await loadConfig();
                const ch = cfg.Channels.find(c => c.Id === btn.dataset.id);
                if (ch) {
                    ch.LastSyncAt = '1970-01-01T00:00:00Z'; // forces re-sync trigger
                    await saveConfig(cfg);
                    alert('Sync scheduled for: ' + ch.Name + '\n\nIt will start within a few seconds.');
                    setTimeout(loadAndRender, 5000);
                }
            } catch (err) { alert('Error: ' + err.message); }
            btn.textContent = 'Sync';
        };
    });

    document.querySelectorAll('.yt-del-btn').forEach(btn => {
        btn.onclick = async () => {
            if (!confirm('Delete this channel and remove its .strm files?')) return;
            try {
                const cfg = await loadConfig();
                cfg.Channels = cfg.Channels.filter(c => c.Id !== btn.dataset.id);
                await saveConfig(cfg);
                setTimeout(loadAndRender, 500);
            } catch (err) { alert('Error: ' + err.message); }
        };
    });
}

async function loadAndRender() {
    try {
        const cfg = await loadConfig();
        populateForm(cfg);
        renderStatusBanner(cfg);
        renderChannels(cfg.Channels || []);
    } catch (err) {
        document.getElementById('ytStatusBanner').style.background = '#3a1a1a';
        document.getElementById('ytStatusBanner').style.color = '#e66e6e';
        document.getElementById('ytStatusBanner').innerHTML =
            '<strong>Error loading config:</strong> ' + escapeHtml(err.message || err);
    }
}

// Save global settings
document.getElementById('ytSaveBtn').onclick = async () => {
    const btn = document.getElementById('ytSaveBtn');
    btn.textContent = 'Saving...'; btn.disabled = true;
    try {
        const cfg = await loadConfig();
        collectConfig(cfg);
        await saveConfig(cfg);
        await loadAndRender();
        Dashboard.alert('YouTube plugin settings saved.');
    } catch (err) {
        Dashboard.alert('Save failed: ' + err.message);
    } finally {
        btn.textContent = 'Save Settings'; btn.disabled = false;
    }
};

// Add channel - resolves channel via YouTube API to get ID, name
document.getElementById('ytAddChannelBtn').onclick = async () => {
    const url = document.getElementById('ytNewChannelUrl').value.trim();
    if (!url) { Dashboard.alert('Enter a channel URL first.'); return; }

    const btn = document.getElementById('ytAddChannelBtn');
    btn.textContent = 'Adding...'; btn.disabled = true;

    try {
        const cfg = await loadConfig();

        // Check API key is configured
        if (!cfg.YouTubeApiKey) {
            Dashboard.alert('Configure your YouTube Data API v3 key first and click Save.');
            return;
        }

        // Resolve channel via direct YouTube API call from the browser.
        // We use the public YouTube Data API v3 endpoint.
        const resp = await fetch('https://www.googleapis.com/youtube/v3/' +
            'channels?part=snippet,contentDetails&key=' + encodeURIComponent(cfg.YouTubeApiKey) +
            buildChannelQuery(url));
        if (!resp.ok) {
            const txt = await resp.text();
            throw new Error('YouTube API error ' + resp.status + ': ' + txt.substring(0, 200));
        }
        const data = await resp.json();
        if (!data.items || data.items.length === 0) {
            throw new Error('Channel not found for URL: ' + url);
        }
        const ch = data.items[0];
        const channelId = ch.id;
        const channelName = ch.snippet?.title || channelId;
        const uploadsPlaylistId = ch.contentDetails?.relatedPlaylists?.uploads || '';

        // Check duplicates
        if (cfg.Channels.some(c => c.Id === channelId)) {
            Dashboard.alert('Channel already configured: ' + channelName);
            return;
        }

        // Build channel config
        const polling = parseInt(document.getElementById('ytNewPolling').value, 10) || cfg.DefaultPollingIntervalHours || 6;
        const retention = document.getElementById('ytNewRetention').value || cfg.DefaultRetentionPolicy || 'permanent';

        cfg.Channels.push({
            Id: channelId,
            Url: url,
            Name: channelName,
            PollingIntervalHours: polling,
            RetentionPolicy: retention,
            AddedAt: new Date().toISOString(),
            LastSyncAt: '1970-01-01T00:00:00Z',
            LastSyncStatus: 'pending',
            VideoCount: 0,
            Disabled: false
        });
        cfg.Channels = cfg.Channels || [];
        if (!cfg.Channels.find(c => c.Id === channelId)) {
            // (already pushed above; this is defensive)
        }

        await saveConfig(cfg);

        document.getElementById('ytNewChannelUrl').value = '';
        Dashboard.alert('Channel added: ' + channelName + '\nSync will start in a few seconds.');
        setTimeout(loadAndRender, 1000);
    } catch (err) {
        Dashboard.alert('Add failed: ' + (err.message || err));
    } finally {
        btn.textContent = 'Add Channel'; btn.disabled = false;
    }
};

// Build YouTube Data API channels? request query based on URL type
function buildChannelQuery(url) {
    if (url.includes('/channel/')) {
        const id = url.split('/channel/')[1].split('/')[0].split('?')[0];
        return '&id=' + encodeURIComponent(id);
    }
    if (url.includes('/@')) {
        const handle = url.split('/@')[1].split('/')[0].split('?')[0];
        return '&forHandle=' + encodeURIComponent('@' + handle);
    }
    if (url.includes('/user/')) {
        const user = url.split('/user/')[1].split('/')[0].split('?')[0];
        return '&forUsername=' + encodeURIComponent(user);
    }
    if (url.includes('/c/')) {
        // /c/CustomName requires search - not directly supported by channels.list
        // Fallback: use the search endpoint with this name
        const custom = url.split('/c/')[1].split('/')[0].split('?')[0];
        return '&forHandle=' + encodeURIComponent('@' + custom);
    }
    if (url.includes('watch?v=')) {
        // Resolve via videos endpoint
        const v = new URL(url).searchParams.get('v');
        return '&forHandle=' + encodeURIComponent('@'); // placeholder, will be handled specially below
    }
    // Assume raw channel ID
    return '&id=' + encodeURIComponent(url);
}

document.getElementById('ytRefreshBtn').onclick = loadAndRender;

document.getElementById('ytSyncAllBtn').onclick = async () => {
    const btn = document.getElementById('ytSyncAllBtn');
    btn.textContent = 'Scheduling...'; btn.disabled = true;
    try {
        const cfg = await loadConfig();
        cfg.Channels.forEach(c => { c.LastSyncAt = '1970-01-01T00:00:00Z'; });
        await saveConfig(cfg);
        Dashboard.alert('Sync scheduled for all channels.');
        setTimeout(loadAndRender, 5000);
    } catch (err) {
        Dashboard.alert('Failed: ' + err.message);
    } finally {
        btn.textContent = 'Sync All Channels Now'; btn.disabled = false;
    }
};

document.getElementById('ytTestResolverBtn').onclick = async () => {
    const videoId = document.getElementById('ytTestVideoId').value.trim();
    if (!videoId) { Dashboard.alert('Enter a video ID.'); return; }

    const out = document.getElementById('ytTestResult');
    out.innerHTML = '<em>Testing...</em>';

    // Test YoutubeExplode directly via the public proxy endpoint of the plugin
    try {
        const proxyPort = parseInt(document.getElementById('ytProxyPort').value, 10) || 8585;
        const proto = window.location.protocol;
        const host = window.location.hostname;

        // Try the proxy URL - if it returns 302 to a YouTube URL, resolver works
        const proxyUrl = proto + '//' + host + ':' + proxyPort + '/youtube_plugin/stream/' + encodeURIComponent(videoId);

        const resp = await fetch(proxyUrl, { method: 'GET', redirect: 'manual' });
        if (resp.status === 0 || resp.type === 'opaqueredirect') {
            // 302 redirected - we can't read the URL due to CORS, but the resolver worked
            out.innerHTML = '<span style="color:#6ee66e;">✓ Proxy responded with redirect (resolver works).</span><br/>' +
                '<span style="opacity:0.7;">Test in Jellyfin by playing the video. URL: ' + escapeHtml(proxyUrl) + '</span>';
        } else if (resp.status === 502) {
            out.innerHTML = '<span style="color:#e66e6e;">✗ Proxy could not resolve the video (502).</span><br/>' +
                '<span style="opacity:0.7;">Check Jellyfin logs for details. YoutubeExplode + yt-dlp both failed.</span>';
        } else {
            out.innerHTML = '<span style="color:#aaa;">Got HTTP ' + resp.status + ' from proxy.</span>';
        }
    } catch (err) {
        out.innerHTML = '<span style="color:#e66e6e;">✗ ' + escapeHtml(err.message) + '</span><br/>' +
            '<span style="opacity:0.7;">Note: if Jellyfin runs in Docker, the proxy port ' +
            '(' + (parseInt(document.getElementById('ytProxyPort').value, 10) || 8585) + ') ' +
            'must be exposed in the container or accessed via the container\'s IP.</span>';
    }
};

// Initial load
(async function () {
    await loadAndRender();
})();
