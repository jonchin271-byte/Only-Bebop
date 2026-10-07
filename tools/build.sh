#!/usr/bin/env bash
# Build the Windows release: preflight -> generate -> extractors -> game -> zip.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GODOT="${GODOT:-godot}"
VERSION="${VERSION:-0.1.0}"
cd "$ROOT"
python3 tools/preflight.py
python3 tools/gen.py
rm -rf dist && mkdir -p dist/OnlyBebop/tools
for x in DeadlockExtract:deadlock-extract OnlyUpExtract:onlyup-extract; do
  proj="${x%%:*}"; name="${x##*:}"
  dotnet publish "extract/$proj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false \
    -p:DebugType=none -o "dist/OnlyBebop/tools/$name" --nologo -v q
done
cp extract/plan.json dist/OnlyBebop/tools/plan.json
# zlib-ng (zlib license) native build used by CUE4Parse for zlib-compressed paks; pinned by hash
ZNG=dist/OnlyBebop/tools/onlyup-extract/zlib-ng2.dll
curl -sSL https://github.com/NotOfficer/Zlib-ng.NET/releases/download/1.0.0/zlib-ng2.dll.gz | gunzip > "$ZNG"
echo "454be2f3d10f804ace577198401431db5e95d0286b59589bc28a40085388e7c2  $ZNG" | sha256sum -c -
(cd game && dotnet build -c ExportRelease --nologo -v q && "$GODOT" --headless --export-release "Windows Desktop" ../dist/OnlyBebop/OnlyBebop.exe)
cp README.md CREDITS.md dist/OnlyBebop/; [ -f LICENSE ] && cp LICENSE dist/OnlyBebop/ || echo "note: no LICENSE yet (license not chosen)"
(cd dist && rm -f "OnlyBebop-$VERSION-win64.zip" && zip -qr "OnlyBebop-$VERSION-win64.zip" OnlyBebop)
ls -la dist
