#!/bin/bash
# Package the Jellyfin YouTube plugin for distribution.
# Excludes DLLs that Jellyfin already ships (to avoid version conflicts).

set -e

PROJECT_DIR="/home/z/my-project/jellyfin-plugin-youtube"
BIN_DIR="$PROJECT_DIR/Jellyfin.Plugin.YouTube/bin/Release/net9.0"
DIST_DIR="$PROJECT_DIR/dist"
VERSION="0.0.0.4-alpha"

# DLLs that Jellyfin already provides - DO NOT include in the zip
# These include all transitive dependencies of Jellyfin.Controller/Model/Extensions:
#   - Jellyfin.* (provided by Jellyfin itself)
#   - MediaBrowser.* (provided by Jellyfin itself)
#   - Microsoft.Extensions.* (provided by Jellyfin runtime)
#   - Microsoft.EntityFrameworkCore.* (provided by Jellyfin.Database.Implementations)
#   - BitFaster.Caching, Diacritics, ICU4N*, J2N, NEbml, Polly* (transitive deps of Jellyfin packages)
EXCLUDE_DLLS=(
    # Jellyfin-provided
    "Emby.Naming.dll"
    "Jellyfin.Data.dll"
    "Jellyfin.Database.Implementations.dll"
    "Jellyfin.Extensions.dll"
    "Jellyfin.MediaEncoding.Keyframes.dll"
    "MediaBrowser.Common.dll"
    "MediaBrowser.Controller.dll"
    "MediaBrowser.Model.dll"
    # Microsoft.Extensions.* (provided by .NET runtime / Jellyfin)
    "Microsoft.Extensions.Caching.Abstractions.dll"
    "Microsoft.Extensions.Caching.Memory.dll"
    "Microsoft.Extensions.Configuration.Abstractions.dll"
    "Microsoft.Extensions.Configuration.Binder.dll"
    "Microsoft.Extensions.DependencyInjection.Abstractions.dll"
    "Microsoft.Extensions.DependencyInjection.dll"
    "Microsoft.Extensions.Logging.Abstractions.dll"
    "Microsoft.Extensions.Logging.dll"
    "Microsoft.Extensions.Options.dll"
    "Microsoft.Extensions.Primitives.dll"
    # Microsoft.EntityFrameworkCore.* (transitive of Jellyfin.Database.Implementations)
    "Microsoft.EntityFrameworkCore.Abstractions.dll"
    "Microsoft.EntityFrameworkCore.Relational.dll"
    "Microsoft.EntityFrameworkCore.dll"
    # Transitive deps of Jellyfin.Extensions / Jellyfin.Controller / Jellyfin.MediaEncoding
    "BitFaster.Caching.dll"
    "Diacritics.dll"
    "ICU4N.dll"
    "ICU4N.Transliterator.dll"
    "J2N.dll"
    "NEbml.Core.dll"
    "Polly.dll"
    "Polly.Core.dll"
)

# Clean dist
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

# Copy our plugin DLL
cp "$BIN_DIR/Jellyfin.Plugin.YouTube.dll" "$DIST_DIR/"

# Copy meta.json (already in project root, sync first)
cp "$PROJECT_DIR/meta.json" "$DIST_DIR/meta.json"

# Copy only the DLLs we need (exclude Jellyfin-provided ones)
for dll in "$BIN_DIR"/*.dll; do
    name=$(basename "$dll")
    if [[ " ${EXCLUDE_DLLS[@]} " =~ " ${name} " ]]; then
        echo "  skip  $name (Jellyfin provides)"
    else
        cp "$dll" "$DIST_DIR/"
        echo "  copy  $name"
    fi
done

# Create the zip
cd "$DIST_DIR"
ZIP_NAME="jellyfin-plugin-youtube_${VERSION}.zip"
rm -f "$ZIP_NAME"
zip -j "$ZIP_NAME" *.dll meta.json

echo ""
echo "=== Package contents ==="
unzip -l "$ZIP_NAME"
echo ""
echo "=== MD5 ==="
md5sum "$ZIP_NAME"

# Copy zip to project root (so it's accessible via raw GitHub URL too)
cp "$ZIP_NAME" "$PROJECT_DIR/"

echo ""
echo "=== Done ==="
echo "Zip: $DIST_DIR/$ZIP_NAME"
echo "Size: $(du -h $DIST_DIR/$ZIP_NAME | cut -f1)"
