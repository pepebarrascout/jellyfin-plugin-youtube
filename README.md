# Jellyfin Plugin: YouTube Streaming

Plugin para Jellyfin que permite reproducir videos de YouTube como si fueran Series nativas, **sin descargarlos al disco**.

## Compatibilidad

**Funciona con Jellyfin 12.1 (y superiores)** — compilado contra .NET 10 y Jellyfin.Controller/Model 12.1.0.

> Versiones anteriores del plugin (0.0.0.1 a 0.0.0.4) son compatibles con Jellyfin 10.11.x y siguen disponibles en el historial de releases.

## Versión actual: v0.0.0.5

Características implementadas:

- ✅ Estructura completa del proyecto .NET 10
- ✅ Plugin entry point con página de configuración web
- ✅ Almacén SQLite propio (no toca la BD de Jellyfin)
- ✅ Cliente `yt-dlp` para resolver canales y listar videos (`--flat-playlist`)
- ✅ Servicio de sincronización con `System.Threading.Timer` por canal
- ✅ Generador de archivos `.strm`
- ✅ Proxy HTTP local que envuelve `yt-dlp` para resolver streams en runtime
- ✅ WatchedTracker suscrito a `ISessionManager.PlaybackStopped`
- ✅ Página web de configuración funcional vía `ApiClient.getPluginConfiguration`/`updatePluginConfiguration`
- ✅ UI y logs completamente en español
- ✅ Feedback visual completo (toasts + estado de loading en botones)
- ✅ Verificación de yt-dlp al arrancar el plugin
- ✅ Sin dependencia de YouTube Data API v3 (sin API key, sin quota)
- ✅ Tamaño del paquete: solo 140 KB

## Stack técnico

- **.NET 10** + Jellyfin Plugin SDK 12.1
- **SQLite** (Microsoft.Data.Sqlite) — estado propio del plugin
- **yt-dlp** — resolución de canales, metadatos y URLs de stream (sin YouTube Data API)
- **System.Threading.Timer** — scheduler por canal con intervalos independientes (built-in .NET, sin Quartz)

## Instalación

### Opción A: Repositorio de plugins (recomendado)

1. En Jellyfin, ve a **Dashboard → Plugins → Repositories**
2. Agrega:
   ```
   https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-youtube/main/manifest.json
   ```
3. Ve al **Catalog**, busca **"Jellyfin YouTube Plugin"** en la categoría **Metadata**
4. Click en el plugin → **Install**
5. Reinicia Jellyfin

### Opción B: Manual (Docker en Raspberry Pi)

1. Descarga el ZIP desde [Releases](https://github.com/pepebarrascout/jellyfin-plugin-youtube/releases)
2. En tu contenedor Docker de Jellyfin, copia el contenido del ZIP al directorio de plugins:
   ```bash
   docker exec -it jellyfin mkdir -p /config/plugins/Jellyfin_Plugin_YouTube_a8c3b2e1
   docker cp Jellyfin.Plugin.YouTube.dll jellyfin:/config/plugins/Jellyfin_Plugin_YouTube_a8c3b2e1/
   docker cp meta.json jellyfin:/config/plugins/Jellyfin_Plugin_YouTube_a8c3b2e1/
   # Copiar también las DLLs dependientes: Microsoft.Data.Sqlite.dll, SQLitePCLRaw.*.dll
   docker restart jellyfin
   ```

## Configuración

### 1. yt-dlp en el contenedor Docker

Tu imagen oficial de Jellyfin NO incluye yt-dlp. Necesitas instalarlo. Como ya lo tienes en el Ubuntu host, lo más fácil es montarlo vía docker-compose:

```yaml
# docker-compose.yml
services:
  jellyfin:
    image: jellyfin/jellyfin:12.1
    volumes:
      - /usr/bin/yt-dlp:/usr/local/bin/yt-dlp:ro
      # ... tus otros volúmenes
```

Luego en la configuración del plugin, pon la ruta como `/usr/local/bin/yt-dlp`.

### 2. Configurar el plugin

1. En Jellyfin: **Dashboard → Plugins → YouTube Plugin → Settings**
2. **Ruta del binario de yt-dlp (REQUERIDO)**: `/usr/local/bin/yt-dlp` (o donde lo hayas montado)
3. **Directorio raíz de .strm**: `/config/youtube_plugin` (default, ya se crea solo)
4. **Puerto del proxy**: 8585 (verifica que esté libre)
5. **Calidad máxima**: 1080p (o la que prefieras)
6. Click **Guardar configuración** — verás un toast verde de confirmación

### 3. Agregar la biblioteca Series

1. Crea un directorio en el host mapeado al contenedor:
   ```bash
   mkdir -p /path/to/jellyfin/config/youtube_plugin
   ```
   (si usas el volumen `/config` de Jellyfin, esto es `/config/youtube_plugin` dentro del contenedor — el plugin lo crea automáticamente)

2. En Jellyfin: **Dashboard → Libraries → Add Media Library**
3. Tipo: **Shows**
4. Nombre: YouTube
5. Carpeta: `/config/youtube_plugin`
6. Click **OK**

### 4. Agregar canales

1. Ve a **Dashboard → Plugins → YouTube Plugin** (página principal del plugin)
2. Pega la URL de un canal YouTube (acepta `/@handle`, `/channel/UCxxxx`, `/c/CustomName`, `/watch?v=...`)
3. Configura polling (horas) y política de retención
4. Click **Agregar canal**
5. Espera ~30 segundos a la primera sincronización

## Arquitectura

```
Usuario → Jellyfin UI → Series library
                              ↓ (al abrir item)
                  .strm file: http://127.0.0.1:8585/youtube_plugin/stream/{videoId}
                              ↓
                  StreamProxy → yt-dlp -g https://youtu.be/{videoId}
                              ↓
                  YouTube stream URL firmada (TTL ~6h)
                              ↓
                  HTTP 302 → Jellyfin reproduce stream directo de YouTube
                              (sin tocar disco, sin transcode intermediario)
```

El plugin **jamás escribe en la base de datos principal de Jellyfin**. Mantiene su propio SQLite en `/config/youtube_plugin/youtube_plugin.sqlite`.

## Roadmap

- [v0.0.0.6] Thumbnail download y cache en disco local
- [v0.0.0.6] Live streams como canal "en vivo"
- [v0.0.0.7] Soporte para videos con restricción de edad (cookies opcionales)
- [v0.0.0.8] Tests automatizados (xUnit)

## Licencia

MIT

## Contribuir

Issues y PRs bienvenidos en https://github.com/pepebarrascout/jellyfin-plugin-youtube
