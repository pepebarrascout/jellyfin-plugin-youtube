// YouTube Streaming plugin configuration page.
// Uses Jellyfin's built-in plugin config endpoints (no custom REST API needed).
// No YouTube Data API key required - yt-dlp handles channel metadata.

const PLUGIN_ID = 'a8c3b2e1-7f4d-4e6a-9b1c-2d5e8f0a1b3c';

function escapeHtml(s) {
    return String(s || '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'})[c]);
}

function fmtDate(s) {
    if (!s) return '';
    try { return new Date(s).toLocaleString(); } catch { return s; }
}

async function loadConfig() {
    return new Promise((resolve, reject) => {
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(resolve).catch(reject);
    });
}

async function saveConfig(cfg) {
    return new Promise((resolve, reject) => {
        ApiClient.updatePluginConfiguration(PLUGIN_ID, cfg).then(() => resolve(true)).catch(reject);
    });
}

function renderStatusBanner(cfg) {
    const banner = document.getElementById('ytStatusBanner');
    const issues = [];
    if (!cfg.YtDlpPath) issues.push('yt-dlp path is not configured (REQUIRED)');
    if (!cfg.StrmRootPath) issues.push('.strm root path is empty');

    if (issues.length === 0) {
        banner.style.background = '#1a3a1a';
        banner.style.border = '1px solid #2d5a2d';
        banner.style.color = '#6ee66e';
        banner.innerHTML = '<strong>OK.</strong> Plugin is configured. ' +
            'yt-dlp: <code>' + escapeHtml(cfg.YtDlpPath || 'yt-dlp') + '</code>. ' +
            'Stream resolver: <code>' + (cfg.PreferYoutubeExplode ? 'YoutubeExplode + yt-dlp fallback' : 'yt-dlp only') + '</code>. ' +
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
    document.getElementById('ytYtDlpPath').value = cfg.YtDlpPath || 'yt-dlp';
    document.getElementById('ytStrmRoot').value = cfg.StrmRootPath || '/config/youtube_plugin';
    document.getElementById('ytProxyPort').value = cfg.StreamProxyPort || 8585;
    document.getElementById('ytMaxQuality').value = cfg.MaxQuality || '1080';
    document.getElementById('ytUseProxy').checked = cfg.UseStreamProxy !== false;
    document.getElementById('ytPreferExplode').checked = cfg.PreferYoutubeExplode !== false;
}

function collectConfig(cfg) {
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
                    ${c.LastSyncAt && c.LastSyncAt !== '1970-01-01T00:00:00Z' ? 'Last sync: ' + fmtDate(c.LastSyncAt) : 'Never synced'}
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
            try {
                const cfg = await loadConfig();
                const ch = cfg.Channels.find(c => c.Id === btn.dataset.id);
                if (ch) {
                    ch.LastSyncAt = '1970-01-01T00:00:00Z';
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

// Add channel - just save URL with placeholder; the sync service resolves it via yt-dlp
document.getElementById('ytAddChannelBtn').onclick = async () => {
    const url = document.getElementById('ytNewChannelUrl').value.trim();
    if (!url) { Dashboard.alert('Enter a channel URL first.'); return; }

    const btn = document.getElementById('ytAddChannelBtn');
    btn.textContent = 'Adding...'; btn.disabled = true;

    try {
        const cfg = await loadConfig();

        // Use URL as temporary ID until first sync resolves the actual channel ID
        const tempId = 'pending-' + Date.now();

        // Check for duplicates by URL
        if (cfg.Channels.some(c => c.Url === url)) {
            Dashboard.alert('Channel already configured: ' + url);
            return;
        }

        const polling = parseInt(document.getElementById('ytNewPolling').value, 10) || cfg.DefaultPollingIntervalHours || 6;
        const retention = document.getElementById('ytNewRetention').value || cfg.DefaultRetentionPolicy || 'permanent';

        cfg.Channels.push({
            Id: tempId,
            Url: url,
            Name: url.split('/').pop() || url,
            PollingIntervalHours: polling,
            RetentionPolicy: retention,
            AddedAt: new Date().toISOString(),
            LastSyncAt: '1970-01-01T00:00:00Z',
            LastSyncStatus: 'pending',
            VideoCount: 0,
            Disabled: false
        });

        await saveConfig(cfg);

        document.getElementById('ytNewChannelUrl').value = '';
        Dashboard.alert('Channel added. Sync will start in a few seconds.\n\nChannel name and ID will be resolved automatically via yt-dlp on first sync.');
        setTimeout(loadAndRender, 3000);
        setTimeout(loadAndRender, 15000);
    } catch (err) {
        Dashboard.alert('Add failed: ' + (err.message || err));
    } finally {
        btn.textContent = 'Add Channel'; btn.disabled = false;
    }
};

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
        setTimeout(loadAndRender, 20000);
    } catch (err) {
        Dashboard.alert('Failed: ' + err.message);
    } finally {
        btn.textContent = 'Sync All Channels Now'; btn.disabled = false;
    }
};

// Test yt-dlp button - we can't invoke yt-dlp directly from the browser
// (sandbox), but we CAN check if the plugin's stream proxy is reachable.
document.getElementById('ytTestYtdlpBtn').onclick = async () => {
    const out = document.getElementById('ytTestYtdlpResult');
    out.innerHTML = '<em>Cannot test yt-dlp directly from browser (sandboxed).</em><br/>' +
        '<em>Check Jellyfin logs for "YouTube plugin: scheduled" or "yt-dlp" entries.</em><br/>' +
        '<em>If a channel was added successfully, yt-dlp is working.</em>';
};

document.getElementById('ytTestResolverBtn').onclick = async () => {
    const videoId = document.getElementById('ytTestVideoId').value.trim();
    if (!videoId) { Dashboard.alert('Enter a video ID.'); return; }

    const out = document.getElementById('ytTestResult');
    out.innerHTML = '<em>Testing...</em>';

    try {
        const proxyPort = parseInt(document.getElementById('ytProxyPort').value, 10) || 8585;
        const proto = window.location.protocol;
        const host = window.location.hostname;
        const proxyUrl = proto + '//' + host + ':' + proxyPort + '/youtube_plugin/stream/' + encodeURIComponent(videoId);

        const resp = await fetch(proxyUrl, { method: 'GET', redirect: 'manual' });
        if (resp.status === 0 || resp.type === 'opaqueredirect') {
            out.innerHTML = '<span style="color:#6ee66e;">✓ Proxy responded with redirect (resolver works).</span><br/>' +
                '<span style="opacity:0.7;">Test in Jellyfin by playing the video.</span>';
        } else if (resp.status === 502) {
            out.innerHTML = '<span style="color:#e66e6e;">✗ Proxy could not resolve the video (502).</span><br/>' +
                '<span style="opacity:0.7;">Check Jellyfin logs. YoutubeExplode + yt-dlp both failed.</span>';
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
