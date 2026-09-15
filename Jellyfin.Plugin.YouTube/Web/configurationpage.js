// YouTube Streaming plugin configuration page logic.
// Calls internal API endpoints exposed by the plugin.

const API_TOKEN = window.ApiClient ? window.ApiClient.accessToken() : '';
const BASE = window.ApiClient ? `${window.ApiClient.serverAddress()}YouTubePlugin` : '';

async function apiCall(method, path, body) {
  const url = `${BASE}${path}`;
  const init = {
    method,
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `MediaBrowser Token="${API_TOKEN}"`
    }
  };
  if (body) init.body = JSON.stringify(body);
  const r = await fetch(url, init);
  let text;
  try { text = await r.text(); } catch { text = ''; }
  let json;
  try { json = text ? JSON.parse(text) : {}; } catch { json = { raw: text }; }
  if (!r.ok) throw new Error(json.error || `HTTP ${r.status}`);
  return json;
}

async function loadStatus() {
  const status = await apiCall('GET', '/status');
  const banner = document.getElementById('ytStatusBanner');
  if (!status.apiKeyConfigured) {
    banner.className = 'yt-status-banner yt-status-error';
    banner.innerHTML = '<b>API key no configurada.</b> Ve a <code>Dashboard &rarr; Plugins &rarr; YouTube Streaming &rarr; Settings</code> e ingresa tu YouTube Data API v3 key.';
  } else {
    banner.className = 'yt-status-banner yt-status-ok';
    banner.innerHTML = `<b>OK.</b> yt-dlp: <code>${status.ytdlpPath}</code> · Proxy: <code>${status.proxyEnabled ? 'puerto ' + status.proxyPort : 'desactivado'}</code> · Ruta .strm: <code>${status.strmRoot}</code>`;
  }
  return status;
}

async function loadChannels() {
  const list = document.getElementById('ytChannelList');
  try {
    const channels = await apiCall('GET', '/channels');
    if (!Array.isArray(channels) || channels.length === 0) {
      list.innerHTML = '<div style="opacity:0.7;text-align:center;padding:20px">Sin canales. Agrega uno arriba.</div>';
      return;
    }
    list.innerHTML = channels.map(c => `
      <div class="yt-channel" data-id="${c.id}">
        <div class="yt-channel-info">
          <div class="name">${escapeHtml(c.name)} <span class="status">${c.videoCount} videos</span></div>
          <div class="meta">
            <a href="${escapeHtml(c.url)}" target="_blank" style="color:inherit;opacity:0.8">${escapeHtml(c.url)}</a><br/>
            Polling: cada ${c.pollingHours}h · Retención: ${c.retention === 'permanent' ? 'permanente' : 'borrar tras 2 días visto'}
            ${c.lastStatus ? '<br/>Último sync: ' + escapeHtml(c.lastStatus) : ''}
            ${c.lastSync ? ' · ' + new Date(c.lastSync).toLocaleString() : ''}
          </div>
        </div>
        <div style="display:flex;gap:8px">
          <button class="yt-btn yt-btn-secondary yt-btn-sm" data-action="sync">Sync</button>
          <button class="yt-btn yt-btn-danger yt-btn-sm" data-action="delete">Eliminar</button>
        </div>
      </div>
    `).join('');
    document.querySelectorAll('.yt-channel').forEach(el => {
      const id = el.getAttribute('data-id');
      el.querySelector('[data-action="sync"]').onclick = async (e) => {
        e.target.textContent = '...';
        try { await apiCall('POST', `/channels/${id}/sync`); }
        catch (err) { alert('Error: ' + err.message); }
        e.target.textContent = 'Sync';
        setTimeout(loadChannels, 3000);
      };
      el.querySelector('[data-action="delete"]').onclick = async () => {
        if (!confirm('¿Eliminar este canal y todos sus .strm files?')) return;
        try { await apiCall('DELETE', `/channels/${id}`); loadChannels(); }
        catch (err) { alert('Error: ' + err.message); }
      };
    });
  } catch (err) {
    list.innerHTML = `<div style="color:#f99">Error: ${escapeHtml(err.message)}</div>`;
  }
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'})[c]);
}

document.getElementById('ytAddBtn').onclick = async () => {
  const url = document.getElementById('ytChannelUrl').value.trim();
  if (!url) { alert('Ingresa una URL'); return; }
  const pollingHours = parseInt(document.getElementById('ytPollingHours').value, 10) || 6;
  const retention = document.getElementById('ytRetention').value;
  const btn = document.getElementById('ytAddBtn');
  btn.textContent = 'Agregando...'; btn.disabled = true;
  try {
    const r = await apiCall('POST', '/channels', { url, pollingHours, retention });
    alert('Canal agregado: ' + r.name + '\n(ID: ' + r.id + ')\nSincronización iniciada en background.');
    document.getElementById('ytChannelUrl').value = '';
    loadChannels();
  } catch (err) {
    alert('Error: ' + err.message);
  } finally {
    btn.textContent = 'Agregar'; btn.disabled = false;
  }
};

document.getElementById('ytRefreshBtn').onclick = async () => {
  await Promise.all([loadStatus(), loadChannels()]);
};

document.getElementById('ytTestYtdlpBtn').onclick = async () => {
  const out = document.getElementById('ytTestResult');
  out.innerHTML = '<span class="yt-spinner"></span> Probando...';
  try {
    const r = await apiCall('POST', '/test-ytdlp', {});
    out.innerHTML = r.ok
      ? `<span style="color:#438659">✓ yt-dlp OK: versión ${escapeHtml(r.version)}</span>`
      : `<span style="color:#f99">✗ ${escapeHtml(r.error)}</span>`;
  } catch (err) {
    out.innerHTML = `<span style="color:#f99">✗ ${escapeHtml(err.message)}</span>`;
  }
};

document.getElementById('ytSyncAllBtn').onclick = async () => {
  const btn = document.getElementById('ytSyncAllBtn');
  btn.textContent = 'Sincronizando...'; btn.disabled = true;
  try {
    const status = await loadStatus();
    for (const c of status.channels) {
      try { await apiCall('POST', `/channels/${c.id}/sync`); } catch {}
    }
    alert('Sincronización en background para todos los canales.');
    setTimeout(loadChannels, 5000);
  } finally {
    btn.textContent = 'Sincronizar todo'; btn.disabled = false;
  }
};

// Initial load
(async () => {
  try {
    await loadStatus();
    await loadChannels();
  } catch (err) {
    document.getElementById('ytStatusBanner').className = 'yt-status-banner yt-status-error';
    document.getElementById('ytStatusBanner').innerHTML =
      `Error inicializando plugin: ${escapeHtml(err.message)}. Verifica que el plugin se haya cargado correctamente.`;
  }
})();
