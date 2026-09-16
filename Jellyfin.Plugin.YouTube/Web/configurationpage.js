// Página de configuración del plugin YouTube Streaming.
// Usa los endpoints nativos de Jellyfin (no requiere API REST custom):
//   ApiClient.getPluginConfiguration(pluginId) -> PluginConfiguration
//   ApiClient.updatePluginConfiguration(pluginId, config) -> guarda en XML

const PLUGIN_ID = 'a8c3b2e1-7f4d-4e6a-9b1c-2d5e8f0a1b3c';

// ==================== Utilidades ====================

function escapeHtml(s) {
    return String(s || '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'})[c]);
}

function fmtDate(s) {
    if (!s) return '';
    try { return new Date(s).toLocaleString('es'); } catch { return s; }
}

// Toast: muestra mensaje flotante durante 4 segundos (verde=éxito, rojo=error, gris=info)
function showToast(message, type) {
    var toast = document.getElementById('ytToast');
    var msg = document.getElementById('ytToastMsg');
    if (!toast || !msg) return;
    msg.innerHTML = message;
    toast.style.display = 'block';
    if (type === 'success') {
        toast.style.background = '#1a3a1a';
        toast.style.borderColor = '#2d5a2d';
        toast.style.color = '#6ee66e';
    } else if (type === 'error') {
        toast.style.background = '#3a1a1a';
        toast.style.borderColor = '#5a2d2d';
        toast.style.color = '#e66e6e';
    } else {
        toast.style.background = '#2a2a2a';
        toast.style.borderColor = '#555';
        toast.style.color = '#ddd';
    }
    clearTimeout(toast._timeoutId);
    toast._timeoutId = setTimeout(function() { toast.style.display = 'none'; }, 4000);
}

// Cambia el texto de un botón temporalmente y lo deshabilita
function setButtonLoading(btn, loadingText) {
    if (!btn._originalText) btn._originalText = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<span>' + loadingText + '</span>';
}

function resetButton(btn) {
    if (btn._originalText) {
        btn.innerHTML = btn._originalText;
        btn.disabled = false;
    }
}

// ==================== API Jellyfin ====================

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

// ==================== Renderizado ====================

function renderStatusBanner(cfg) {
    var banner = document.getElementById('ytStatusBanner');
    var issues = [];
    if (!cfg.YtDlpPath) issues.push('La ruta de yt-dlp no está configurada (REQUERIDO)');
    if (!cfg.StrmRootPath) issues.push('La ruta raíz de .strm está vacía');

    if (issues.length === 0) {
        banner.style.background = '#1a3a1a';
        banner.style.border = '1px solid #2d5a2d';
        banner.style.color = '#6ee66e';
        banner.innerHTML = '<strong>OK.</strong> Plugin configurado. ' +
            'yt-dlp: <code>' + escapeHtml(cfg.YtDlpPath || 'yt-dlp') + '</code>. ' +
            'Proxy: ' + (cfg.UseStreamProxy ? 'puerto ' + cfg.StreamProxyPort : 'desactivado') + '. ' +
            'Raíz .strm: <code>' + escapeHtml(cfg.StrmRootPath) + '</code>.';
    } else {
        banner.style.background = '#3a1a1a';
        banner.style.border = '1px solid #5a2d2d';
        banner.style.color = '#e66e6e';
        banner.innerHTML = '<strong>Problemas de configuración:</strong><ul style="margin:6px 0 0 20px;">' +
            issues.map(i => '<li>' + escapeHtml(i) + '</li>').join('') + '</ul>';
    }
}

function populateForm(cfg) {
    document.getElementById('ytYtDlpPath').value = cfg.YtDlpPath || 'yt-dlp';
    document.getElementById('ytStrmRoot').value = cfg.StrmRootPath || '/config/youtube_plugin';
    document.getElementById('ytProxyPort').value = cfg.StreamProxyPort || 8585;
    document.getElementById('ytMaxQuality').value = cfg.MaxQuality || '1080';
    document.getElementById('ytUseProxy').checked = cfg.UseStreamProxy !== false;
}

function collectConfig(cfg) {
    cfg.YtDlpPath = document.getElementById('ytYtDlpPath').value.trim() || 'yt-dlp';
    cfg.StrmRootPath = document.getElementById('ytStrmRoot').value.trim() || '/config/youtube_plugin';
    cfg.StreamProxyPort = parseInt(document.getElementById('ytProxyPort').value, 10) || 8585;
    cfg.MaxQuality = document.getElementById('ytMaxQuality').value;
    cfg.UseStreamProxy = document.getElementById('ytUseProxy').checked;
    return cfg;
}

function renderChannels(channels) {
    var list = document.getElementById('ytChannelList');
    if (!channels || channels.length === 0) {
        list.innerHTML = '<div style="opacity:0.7;text-align:center;padding:20px;">Sin canales configurados. Agrega uno arriba.</div>';
        return;
    }
    list.innerHTML = channels.map(function (c) {
        var statusColor = (c.LastSyncStatus || '').startsWith('ok') ? '#6ee66e' :
                          (c.LastSyncStatus || '').startsWith('error') ? '#e66e6e' : '#aaa';
        var lastSyncText = '';
        if (c.LastSyncAt && c.LastSyncAt !== '1970-01-01T00:00:00Z') {
            lastSyncText = 'Última sincronización: ' + fmtDate(c.LastSyncAt);
        } else {
            lastSyncText = 'Nunca sincronizado';
        }
        return '<div style="display:flex;justify-content:space-between;align-items:center;padding:12px;border-bottom:1px solid #333;">' +
            '<div style="flex:1;">' +
                '<div style="font-weight:600;">' + escapeHtml(c.Name || c.Id) +
                    '<span style="font-size:0.8rem;opacity:0.6;margin-left:8px;">' + (c.VideoCount || 0) + ' videos</span>' +
                    (c.Disabled ? '<span style="font-size:0.7rem;color:#e66e6e;margin-left:8px;">[deshabilitado]</span>' : '') +
                '</div>' +
                '<div style="font-size:0.8rem;opacity:0.7;margin-top:4px;">' +
                    '<a href="' + escapeHtml(c.Url) + '" target="_blank" style="color:#5b9bd5;">' + escapeHtml(c.Url) + '</a>' +
                '</div>' +
                '<div style="font-size:0.75rem;opacity:0.6;margin-top:4px;">' +
                    'Polling: cada ' + c.PollingIntervalHours + 'h &middot; ' +
                    'Retención: ' + (c.RetentionPolicy === 'permanent' ? 'permanente' : 'eliminar tras 2 días visto') + '<br/>' +
                    lastSyncText +
                    (c.LastSyncStatus ? ' &middot; <span style="color:' + statusColor + ';">' + escapeHtml(c.LastSyncStatus) + '</span>' : '') +
                '</div>' +
            '</div>' +
            '<div style="display:flex;gap:6px;flex-shrink:0;">' +
                '<button class="yt-sync-btn raised emby-button" data-id="' + escapeHtml(c.Id) + '" type="button" style="padding:4px 12px;font-size:0.8rem;">Sync</button>' +
                '<button class="yt-del-btn emby-button" data-id="' + escapeHtml(c.Id) + '" type="button" style="padding:4px 12px;font-size:0.8rem;background:#c44;color:#fff;">Eliminar</button>' +
            '</div>' +
        '</div>';
    }).join('');

    document.querySelectorAll('.yt-sync-btn').forEach(btn => {
        btn.onclick = async () => {
            setButtonLoading(btn, 'Sincronizando...');
            try {
                var cfg = await loadConfig();
                var ch = cfg.Channels.find(c => c.Id === btn.dataset.id);
                if (ch) {
                    ch.LastSyncAt = '1970-01-01T00:00:00Z';
                    await saveConfig(cfg);
                    showToast('Sincronización programada para: <strong>' + escapeHtml(ch.Name) + '</strong><br/>Comenzará en unos segundos.', 'success');
                    setTimeout(loadAndRender, 5000);
                }
            } catch (err) {
                showToast('Error: ' + escapeHtml(err.message || err), 'error');
            } finally {
                resetButton(btn);
            }
        };
    });

    document.querySelectorAll('.yt-del-btn').forEach(btn => {
        btn.onclick = async () => {
            if (!confirm('¿Eliminar este canal y borrar sus archivos .strm?')) return;
            setButtonLoading(btn, 'Eliminando...');
            try {
                var cfg = await loadConfig();
                cfg.Channels = cfg.Channels.filter(c => c.Id !== btn.dataset.id);
                await saveConfig(cfg);
                showToast('Canal eliminado.', 'success');
                setTimeout(loadAndRender, 500);
            } catch (err) {
                showToast('Error al eliminar: ' + escapeHtml(err.message || err), 'error');
            } finally {
                resetButton(btn);
            }
        };
    });
}

async function loadAndRender() {
    try {
        var cfg = await loadConfig();
        populateForm(cfg);
        renderStatusBanner(cfg);
        renderChannels(cfg.Channels || []);
    } catch (err) {
        document.getElementById('ytStatusBanner').style.background = '#3a1a1a';
        document.getElementById('ytStatusBanner').style.color = '#e66e6e';
        document.getElementById('ytStatusBanner').innerHTML =
            '<strong>Error cargando configuración:</strong> ' + escapeHtml(err.message || err);
    }
}

// ==================== Handlers de botones ====================

// Guardar configuración
document.getElementById('ytSaveBtn').onclick = async () => {
    var btn = document.getElementById('ytSaveBtn');
    setButtonLoading(btn, 'Guardando...');
    try {
        var cfg = await loadConfig();
        collectConfig(cfg);
        await saveConfig(cfg);
        await loadAndRender();
        showToast('Configuración guardada correctamente.', 'success');
    } catch (err) {
        showToast('Error al guardar: ' + escapeHtml(err.message || err), 'error');
    } finally {
        resetButton(btn);
    }
};

// Agregar canal
document.getElementById('ytAddChannelBtn').onclick = async () => {
    var url = document.getElementById('ytNewChannelUrl').value.trim();
    if (!url) {
        showToast('Ingresa una URL de canal primero.', 'error');
        return;
    }

    var btn = document.getElementById('ytAddChannelBtn');
    setButtonLoading(btn, 'Agregando...');

    try {
        var cfg = await loadConfig();
        var tempId = 'pending-' + Date.now();

        // Verificar duplicados por URL
        if (cfg.Channels.some(c => c.Url === url)) {
            showToast('El canal ya está configurado: ' + escapeHtml(url), 'error');
            resetButton(btn);
            return;
        }

        var polling = parseInt(document.getElementById('ytNewPolling').value, 10) || cfg.DefaultPollingIntervalHours || 6;
        var retention = document.getElementById('ytNewRetention').value || cfg.DefaultRetentionPolicy || 'permanent';

        cfg.Channels.push({
            Id: tempId,
            Url: url,
            Name: url.split('/').pop() || url,
            PollingIntervalHours: polling,
            RetentionPolicy: retention,
            AddedAt: new Date().toISOString(),
            LastSyncAt: '1970-01-01T00:00:00Z',
            LastSyncStatus: 'pendiente',
            VideoCount: 0,
            Disabled: false
        });

        await saveConfig(cfg);

        document.getElementById('ytNewChannelUrl').value = '';
        showToast('Canal agregado: <strong>' + escapeHtml(url) + '</strong><br/>La sincronización comenzará en unos segundos.<br/>El nombre real del canal se resolverá automáticamente con yt-dlp.', 'success');
        setTimeout(loadAndRender, 3000);
        setTimeout(loadAndRender, 15000);
    } catch (err) {
        showToast('Error al agregar canal: ' + escapeHtml(err.message || err), 'error');
    } finally {
        resetButton(btn);
    }
};

// Refrescar
document.getElementById('ytRefreshBtn').onclick = async () => {
    var btn = document.getElementById('ytRefreshBtn');
    setButtonLoading(btn, 'Refrescando...');
    try {
        await loadAndRender();
        showToast('Vista refrescada.', 'success');
    } catch (err) {
        showToast('Error: ' + escapeHtml(err.message || err), 'error');
    } finally {
        resetButton(btn);
    }
};

// Sincronizar todos los canales
document.getElementById('ytSyncAllBtn').onclick = async () => {
    var btn = document.getElementById('ytSyncAllBtn');
    setButtonLoading(btn, 'Programando...');
    try {
        var cfg = await loadConfig();
        if (!cfg.Channels || cfg.Channels.length === 0) {
            showToast('No hay canales para sincronizar.', 'error');
            return;
        }
        cfg.Channels.forEach(c => { c.LastSyncAt = '1970-01-01T00:00:00Z'; });
        await saveConfig(cfg);
        showToast('Sincronización programada para ' + cfg.Channels.length + ' canal(es).<br/>Actualiza en ~15 segundos para ver el resultado.', 'success');
        setTimeout(loadAndRender, 5000);
        setTimeout(loadAndRender, 20000);
    } catch (err) {
        showToast('Error: ' + escapeHtml(err.message || err), 'error');
    } finally {
        resetButton(btn);
    }
};

// Probar yt-dlp
document.getElementById('ytTestYtdlpBtn').onclick = async () => {
    var btn = document.getElementById('ytTestYtdlpBtn');
    setButtonLoading(btn, 'Probando...');
    var out = document.getElementById('ytTestYtdlpResult');
    out.innerHTML = '<em style="color:#aaa;">No se puede probar yt-dlp directamente desde el navegador (sandboxed).</em><br/>' +
        '<em>Verifica los logs de Jellyfin buscando "yt-dlp detectado".</em><br/>' +
        '<em>Si un canal se agregó exitosamente, yt-dlp está funcionando.</em>';
    showToast('Para verificar yt-dlp, agrega un canal y revisa si aparece la lista de videos.<br/>También revisa los logs de Jellyfin.', 'success');
    setTimeout(() => resetButton(btn), 1500);
};

// Probar resolver de stream
document.getElementById('ytTestResolverBtn').onclick = async () => {
    var videoId = document.getElementById('ytTestVideoId').value.trim();
    if (!videoId) {
        showToast('Ingresa un ID de video.', 'error');
        return;
    }

    var btn = document.getElementById('ytTestResolverBtn');
    setButtonLoading(btn, 'Probando...');
    var out = document.getElementById('ytTestResult');
    out.innerHTML = '<em style="color:#aaa;">Probando resolución de stream...</em>';

    try {
        var proxyPort = parseInt(document.getElementById('ytProxyPort').value, 10) || 8585;
        var proto = window.location.protocol;
        var host = window.location.hostname;
        var proxyUrl = proto + '//' + host + ':' + proxyPort + '/youtube_plugin/stream/' + encodeURIComponent(videoId);

        var resp = await fetch(proxyUrl, { method: 'GET', redirect: 'manual' });
        if (resp.status === 0 || resp.type === 'opaqueredirect') {
            out.innerHTML = '<span style="color:#6ee66e;">✓ El proxy respondió con una redirección (el resolver funciona).</span><br/>' +
                '<span style="opacity:0.7;">Prueba reproducir el video en Jellyfin.</span>';
            showToast('Resolución de stream OK.', 'success');
        } else if (resp.status === 502) {
            out.innerHTML = '<span style="color:#e66e6e;">✗ El proxy no pudo resolver el video (502).</span><br/>' +
                '<span style="opacity:0.7;">Revisa los logs de Jellyfin. yt-dlp falló resolviendo la URL.</span>';
            showToast('Resolución de stream fallida. Revisa los logs.', 'error');
        } else {
            out.innerHTML = '<span style="color:#aaa;">El proxy respondió HTTP ' + resp.status + '.</span>';
            showToast('Respuesta inesperada del proxy: HTTP ' + resp.status, 'error');
        }
    } catch (err) {
        out.innerHTML = '<span style="color:#e66e6e;">✗ ' + escapeHtml(err.message) + '</span><br/>' +
            '<span style="opacity:0.7;">Si Jellyfin corre en Docker, el puerto del proxy (' +
            (parseInt(document.getElementById('ytProxyPort').value, 10) || 8585) + ') ' +
            'debe estar expuesto en el contenedor o accesible vía la IP del contenedor.</span>';
        showToast('Error: ' + escapeHtml(err.message), 'error');
    } finally {
        resetButton(btn);
    }
};

// Carga inicial
(async function () {
    await loadAndRender();
})();
