# Jellyfin Plugin: YouTube Streaming

Plugin para Jellyfin 10.11 que permite reproducir videos de YouTube como si fueran Series nativas, **sin descargarlos al disco**.

## Versión actual: v0.0.0.1 (scaffold inicial)

Esta es la primera iteración del plugin. Incluye:
- Estructura completa del proyecto .NET 9
- Plugin entry point con página de configuración web
- Almacén SQLite propio (no toca la BD de Jellyfin)
- Cliente de YouTube Data API v3 (resolución de canales + listado de videos)
- Servicio de sincronización con scheduler Quartz.NET por canal
- Generador de archivos `.strm`
- Proxy HTTP local que envuelve yt-dlp para resolver streams en runtime
- Watched tracker (skeleton)

**Lo que aún no está completo** (próximas iteraciones):
- Endpoints REST de la página de configuración (Jellyfin controller pattern)
- Subscripción a eventos PlaybackStop del SessionManager de Jellyfin
- Descarga y cache de miniaturas en disco local
- Tests automatizados

## Instalación

### Opción A: Repositorio de plugins (recomendado)

1. En Jellyfin, ve a **Dashboard → Plugins → Repositories**
2. Agrega:
   ```
   https://github.com/pepebarrascout/jellyfin-plugin-youtube/raw/main/manifest.json
   ```
3. Ve al **Catalog**, busca **YouTube Streaming**, e instala
4. Reinicia Jellyfin

### Opción B: Manual (Docker en Raspberry Pi)

1. Descarga el ZIP desde [Releases](https://github.com/pepebarrascout/jellyfin-plugin-youtube/releases)
2. En tu contenedor Docker de Jellyfin, monta el directorio de plugins:
   ```bash
   docker exec -it jellyfin mkdir -p /config/plugins/YouTubeStreaming_a8c3b2e1
   docker cp Jellyfin.Plugin.YouTube-v0.0.0.1.zip jellyfin:/config/plugins/YouTubeStreaming_a8c3b2e1/
   docker exec -it jellyfin unzip -o /config/plugins/YouTubeStreaming_a8c3b2e1/Jellyfin.Plugin.YouTube-v0.0.0.1.zip -d /config/plugins/YouTubeStreaming_a8c3b2e1/
   docker exec -it jellyfin rm /config/plugins/YouTubeStreaming_a8c3b2e1/Jellyfin.Plugin.YouTube-v0.0.0.1.zip
   docker restart jellyfin
   ```

## Configuración

### 1. YouTube Data API v3 Key

1. Ve a [Google Cloud Console](https://console.cloud.google.com/)
2. Crea un proyecto nuevo (o usa uno existente)
3. Habilita **YouTube Data API v3**
4. Crea una API key (Credentials → Create credentials → API key)
5. Cuota gratuita: 10,000 unidades/día (suficiente para 50+ canales con polling cada 6h)

### 2. yt-dlp en el contenedor Docker

Tu imagen oficial de Jellyfin NO incluye yt-dlp. Necesitas instalarlo. Opciones:

**Opción A — Imagen personalizada (recomendada)**:
```dockerfile
FROM jellyfin/jellyfin:10.11.11
USER root
RUN apt-get update && apt-get install -y python3 python3-pip curl && \
    pip3 install --break-system-packages yt-dlp && \
    apt-get clean && rm -rf /var/lib/apt/lists/*
```

**Opción B — Instalar en runtime (efímero)**:
```bash
docker exec -it jellyfin bash -c "apt-get update && apt-get install -y python3 python3-pip && pip3 install yt-dlp"
```

### 3. Configurar el plugin

1. En Jellyfin: **Dashboard → Plugins → YouTube Streaming → Settings**
2. Pega tu YouTube Data API v3 key
3. Verifica la ruta de yt-dlp (por defecto `yt-dlp` asume que está en PATH)
4. Configura el puerto del proxy (default 8585)
5. Guarda y reinicia Jellyfin

### 4. Agregar la biblioteca Series

1. Crea un directorio en el host mapeado al contenedor:
   ```bash
   mkdir -p /path/to/jellyfin/config/youtube_plugin
   ```
   (si usas el volumen `/config` de Jellyfin, esto es `/config/youtube_plugin` dentro del contenedor)

2. En Jellyfin: **Dashboard → Libraries → Add Media Library**
3. Tipo: **Shows**
4. Nombre: YouTube
5. Carpeta: `/config/youtube_plugin`
6. Click **OK**

### 5. Agregar canales

1. Ve a **Dashboard → Plugins → YouTube Streaming** (página principal del plugin)
2. Pega la URL de un canal YouTube (acepta `/@handle`, `/channel/UCxxxx`, `/c/CustomName`, `/watch?v=...`)
3. Configura polling (horas) y política de retención
4. Click **Agregar**
5. Espera ~30 segundos a la primera sincronización

## Stack técnico

- **.NET 9** + Jellyfin Plugin SDK 10.11
- **SQLite** (Microsoft.Data.Sqlite) — estado propio del plugin
- **YouTube Data API v3** (Google.Apis.YouTube.v3) — metadatos de canales y videos
- **yt-dlp** — resolución de stream URLs en tiempo de reproducción
- **Quartz.NET** — scheduler por canal con intervalos independientes
- **Polly** — políticas de retry/backoff para llamadas a YouTube API

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

## Licencia

MIT

## Contribuir

Issues y PRs bienvenidos en https://github.com/pepebarrascout/jellyfin-plugin-youtube

## Roadmap

- [v0.0.0.2] REST API endpoints (Jellyfin controller pattern)
- [v0.0.0.2] Watched tracker completo (PlaybackStop event wiring)
- [v0.0.0.3] Descarga y cache de miniaturas en disco local
- [v0.0.0.3] Thumbnails automáticos como posters de Series
- [v0.0.0.4] Tests automatizados (xUnit)
- [v0.0.0.5] Soporte para videos con restricción de edad (cookies opcionales)
- [v0.0.0.6] Live streams como canal "en vivo"
