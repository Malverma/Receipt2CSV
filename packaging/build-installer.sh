#!/usr/bin/env bash
# Builds dist/POSscan-Setup-<version>.exe from Linux.
# Needs: .NET 10 SDK, wine, cabextract, python3, unzip, curl.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/posscan-build"

# NSIS compiles the installer. Its licence allows commercial use.
NSIS_URL="https://downloads.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip"
NSIS_SHA256="c7d27f780ddb6cffb4730138cd1591e841f4b7edb155856901cdf5f214394fa1"

# Microsoft's Visual C++ runtime 14.44. onnxruntime.dll needs it and the .NET
# publish doesn't include it. Wine supplies its own copy, so a missing runtime
# only shows up on real Windows.
VC_URL="https://download.visualstudio.microsoft.com/download/pr/990c720a-46ce-4dc7-87b9-c2eb615d34b9/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe"
VC_SHA256="cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b"
VC_DLLS=(msvcp140 msvcp140_1 vcruntime140 vcruntime140_1)

[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"

fetch() { # url sha256 destination
  if [ ! -f "$3" ] || ! echo "$2  $3" | sha256sum -c --status; then
    curl -fsSL --retry 3 -o "$3.tmp" "$1"
    echo "$2  $3.tmp" | sha256sum -c --quiet
    mv "$3.tmp" "$3"
  fi
}

mkdir -p "$CACHE" "$ROOT/dist"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/POSscan.App/POSscan.App.csproj")"
[ -n "$VERSION" ] || { echo "No <Version> in POSscan.App.csproj" >&2; exit 1; }

echo "== Publishing POSscan $VERSION"
PUBLISH="$ROOT/publish/win-x64"
rm -rf "$PUBLISH"
dotnet publish "$ROOT/src/POSscan.App" -c Release -r win-x64 --self-contained true \
  -p:DebugType=None -o "$PUBLISH" --nologo -v quiet

echo "== Getting build tools"
fetch "$NSIS_URL" "$NSIS_SHA256" "$CACHE/nsis.zip"
fetch "$VC_URL" "$VC_SHA256" "$CACHE/vc_redist.x64.exe"
if [ ! -d "$CACHE/nsis-3.11" ]; then
  unzip -q "$CACHE/nsis.zip" -d "$CACHE"
fi

echo "== Staging files"
STAGE="$ROOT/publish/stage"
rm -rf "$STAGE"
cp -r "$PUBLISH" "$STAGE"
# Debug symbols and link libraries come from NuGet packages and are over 100 MB.
find "$STAGE" -type f \( -name '*.pdb' -o -name '*.lib' \) -delete

VC_TMP="$(mktemp -d)"
trap 'rm -rf "$VC_TMP"' EXIT
python3 -I "$ROOT/packaging/carve_cabs.py" "$CACHE/vc_redist.x64.exe" "$VC_TMP"
# The redistributable holds a cabinet of cabinets. a12 is the x64 minimum runtime.
cabextract -q -d "$VC_TMP/outer" "$VC_TMP/1.cab"
cabextract -q -d "$VC_TMP/inner" "$VC_TMP/outer/a12"
for dll in "${VC_DLLS[@]}"; do
  cp "$VC_TMP/inner/$dll.dll_amd64" "$STAGE/$dll.dll"
  objdump -f "$STAGE/$dll.dll" | grep -q 'x86-64' || { echo "$dll.dll is not x64" >&2; exit 1; }
done

echo "== Compiling installer"
OUT="POSscan-Setup-$VERSION.exe"
SIZE_KB="$(du -sk "$STAGE" | cut -f1)"
export WINEPREFIX="$CACHE/wine" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d" WINEARCH=win64
cd "$ROOT/packaging"
wine "$CACHE/nsis-3.11/Bin/makensis.exe" -V2 -WX \
  "-DVERSION=$VERSION" '-DSTAGE=..\publish\stage' "-DOUTFILE=..\\dist\\$OUT" "-DSIZE_KB=$SIZE_KB" \
  installer.nsi

echo
ls -la "$ROOT/dist/$OUT"
sha256sum "$ROOT/dist/$OUT"
