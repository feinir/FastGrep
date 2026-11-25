#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <version>  (example: $0 1.1)" >&2
  exit 2
fi

version="$1"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
  echo "Invalid version '$version'. Expected forms such as 1.1 or 1.1.0." >&2
  exit 2
fi

project_root="$(cd "$(dirname "$0")" && pwd)"
workspace_root="$(cd "$project_root/.." && pwd)"
desktop_project="$project_root/src/FastGrep.Desktop/FastGrep.Desktop.csproj"
test_project="$project_root/tests/FastGrep.Tests/FastGrep.Tests.csproj"
asset_dir="$project_root/src/FastGrep.Desktop/Assets"
release_dir="$workspace_root/Release/$version"
temp_root="$(mktemp -d /tmp/fastgrep-release.XXXXXX)"
staged_archives=""

cleanup() {
  rm -rf "$temp_root"
  if [[ -n "$staged_archives" ]]; then
    rm -rf "$staged_archives"
  fi
}
trap cleanup EXIT

for tool in dotnet qlmanage python3 plutil tar zip unzip shasum xattr codesign; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "Missing required tool: $tool" >&2
    exit 1
  }
done

python3 -c 'import PIL' >/dev/null 2>&1 || {
  echo "Missing required Python package: Pillow" >&2
  exit 1
}

mkdir -p "$release_dir" "$temp_root/icon-render" "$temp_root/archives"

qlmanage -t -s 1024 -o "$temp_root/icon-render" "$asset_dir/app-icon.svg" >/dev/null
python3 "$project_root/packaging/generate-icons.py" "$temp_root/icon-render/app-icon.svg.png" "$asset_dir"

dotnet run --project "$test_project" -c Release --artifacts-path "$temp_root/test-build"

runtime_args=()
if [[ -n "${FASTGREP_RUNTIME_VERSION:-}" ]]; then
  runtime_args+=("-p:RuntimeFrameworkVersion=$FASTGREP_RUNTIME_VERSION")
fi
for rid in linux-x64 win-x64 osx-arm64; do
  dotnet publish "$desktop_project" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=false \
    -p:Version="$version" \
    -p:NuGetAudit=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    ${runtime_args[@]+"${runtime_args[@]}"} \
    --artifacts-path "$temp_root/build/$rid" \
    -o "$temp_root/publish/$rid"
  find "$temp_root/publish/$rid" -type f -name '*.pdb' -delete
done

linux_root="$temp_root/package/FastGrep-$version-linux-x64"
windows_root="$temp_root/package/FastGrep-$version-windows-x64"
mac_app="$temp_root/package/FastGrep.app"
mac_plist="$temp_root/Info.plist"

mkdir -p "$linux_root" "$windows_root" "$mac_app/Contents/MacOS" "$mac_app/Contents/Resources"
cp -R "$temp_root/publish/linux-x64/." "$linux_root/"
cp -R "$temp_root/publish/win-x64/." "$windows_root/"
cp -R "$temp_root/publish/osx-arm64/." "$mac_app/Contents/MacOS/"
cp "$asset_dir/app-icon.icns" "$mac_app/Contents/Resources/app-icon.icns"
cp "$project_root/LICENSE" "$linux_root/LICENSE"
cp "$project_root/LICENSE" "$windows_root/LICENSE"
cp "$project_root/LICENSE" "$mac_app/Contents/Resources/LICENSE"
collect_notices() {
  python3 "$project_root/packaging/collect-notices.py" \
    "$temp_root/build/$1/obj/FastGrep.Desktop/project.assets.json" "$2/ThirdPartyNotices"
}
collect_notices linux-x64 "$linux_root"
collect_notices win-x64 "$windows_root"
collect_notices osx-arm64 "$mac_app/Contents/Resources"
if [[ -d "$mac_app/Contents/MacOS/Assets" ]]; then
  mv "$mac_app/Contents/MacOS/Assets" "$mac_app/Contents/Resources/Assets"
  ln -s ../Resources/Assets "$mac_app/Contents/MacOS/Assets"
fi
cp "$project_root/packaging/macos/Info.plist" "$mac_plist"
plutil -replace CFBundleShortVersionString -string "$version" "$mac_plist"
plutil -replace CFBundleVersion -string "$version" "$mac_plist"
cp "$mac_plist" "$mac_app/Contents/Info.plist"
chmod +x "$linux_root/FastGrep.Desktop" "$mac_app/Contents/MacOS/FastGrep.Desktop"
xattr -cr "$linux_root" "$windows_root" "$mac_app"
codesign --force --deep --sign - "$mac_app"
codesign --verify --deep --strict "$mac_app"

linux_archive="$temp_root/archives/FastGrep-$version-linux-x64.tar.gz"
windows_archive="$temp_root/archives/FastGrep-$version-windows-x64.zip"
mac_archive="$temp_root/archives/FastGrep-$version-macos-arm64.zip"

COPYFILE_DISABLE=1 tar --no-xattrs -C "$temp_root/package" -czf "$linux_archive" "$(basename "$linux_root")"
(
  cd "$temp_root/package"
  zip -q -r "$windows_archive" "$(basename "$windows_root")"
)
(
  cd "$temp_root/package"
  COPYFILE_DISABLE=1 zip -q -X -y -r "$mac_archive" "$(basename "$mac_app")"
)

test -s "$linux_archive"
test -s "$windows_archive"
test -s "$mac_archive"
tar -tzf "$linux_archive" >/dev/null
unzip -tq "$windows_archive" >/dev/null
unzip -tq "$mac_archive" >/dev/null

staged_archives="$(mktemp -d "$release_dir/.archives.XXXXXX")"
cp "$linux_archive" "$windows_archive" "$mac_archive" "$staged_archives/"
mv "$staged_archives/"* "$release_dir/"
(
  cd "$release_dir"
  shasum -a 256 "FastGrep-$version-linux-x64.tar.gz" "FastGrep-$version-windows-x64.zip" "FastGrep-$version-macos-arm64.zip"
)
