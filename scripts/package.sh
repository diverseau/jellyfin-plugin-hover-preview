#!/usr/bin/env bash
# Builds the plugin, zips it for a Jellyfin plugin repository and prints the manifest entry.
# Usage: scripts/package.sh            (version comes from Directory.Build.props)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

version="$(sed -n 's:.*<HoverPreviewVersion>\(.*\)</HoverPreviewVersion>.*:\1:p' Directory.Build.props)"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "HoverPreviewVersion in Directory.Build.props must be SemVer major.minor.patch, got '$version'" >&2
  exit 1
fi
abi="10.11.0.0"
out="$root/artifacts"
stage="$out/stage"
zip="$out/hover-preview_${version}.zip"

rm -rf "$out"
mkdir -p "$stage"

dotnet test --nologo -c Release
dotnet publish src/Jellyfin.Plugin.HoverPreview -c Release -o "$out/publish" --nologo

cp "$out/publish/Jellyfin.Plugin.HoverPreview.dll" "$stage/"
timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
cat > "$stage/meta.json" <<JSON
{
  "category": "General",
  "changelog": "See https://github.com/diverseau/jellyfin-plugin-hover-preview/releases/tag/v${version}",
  "description": "Netflix-style pop-out cards with muted preview clips on hover.",
  "guid": "4f7ecbe9-9f13-44e5-814d-63e5ad579f8c",
  "name": "Hover Preview",
  "overview": "Netflix-style pop-out cards with muted preview clips on hover",
  "owner": "diverseau",
  "targetAbi": "${abi}",
  "timestamp": "${timestamp}",
  "version": "${version}.0",
  "status": "Active",
  "autoUpdate": true,
  "assemblies": []
}
JSON

(cd "$stage" && zip -q -X "$zip" Jellyfin.Plugin.HoverPreview.dll meta.json)
checksum="$(md5sum "$zip" | cut -d' ' -f1)"

echo "Built $zip (md5 $checksum)"

# Add (or replace) this version in the plugin repository manifest that Jellyfin reads.
python3 - "$root/manifest.json" "$version" "$abi" "$checksum" "$timestamp" <<'PY'
import json, os, sys
path, version, abi, checksum, timestamp = sys.argv[1:]
manifest = json.load(open(path)) if os.path.exists(path) else [{
    "guid": "4f7ecbe9-9f13-44e5-814d-63e5ad579f8c",
    "name": "Hover Preview",
    "overview": "Netflix-style pop-out cards with muted preview clips on hover",
    "description": "Rest the mouse on any card in jellyfin-web and a larger card pops out with the backdrop, title logo, Play / favourite / like / details buttons and metadata, then plays a short muted, spoiler-safe preview clip.",
    "owner": "diverseau",
    "category": "General",
    "versions": [],
}]
entry = {
    "version": version + ".0",
    "changelog": "https://github.com/diverseau/jellyfin-plugin-hover-preview/releases/tag/v" + version,
    "targetAbi": abi,
    "sourceUrl": "https://github.com/diverseau/jellyfin-plugin-hover-preview/releases/download/v%s/hover-preview_%s.zip" % (version, version),
    "checksum": checksum,
    "timestamp": timestamp,
}
plugin = manifest[0]
plugin["versions"] = [entry] + [v for v in plugin["versions"] if v["version"] != entry["version"]]
with open(path, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
print("Updated manifest.json for", entry["version"])
PY
