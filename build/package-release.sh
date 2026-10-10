#!/usr/bin/env bash
# Package one publish output and record its exact Git commit.
# Usage: bash build/package-release.sh PUBLISH_DIR VERSION RID COMMIT OUTPUT_DIR
set -euo pipefail

if (( $# != 5 )); then
  echo "Usage: $0 PUBLISH_DIR VERSION RID COMMIT OUTPUT_DIR" >&2
  exit 2
fi

publish_dir="$(cd "$1" && pwd)"
version="$2"
rid="$3"
commit="$4"
mkdir -p "$5"
output_dir="$(cd "$5" && pwd)"
repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || {
  echo "Invalid release version: $version" >&2
  exit 2
}
[[ "$commit" =~ ^[a-fA-F0-9]{40}$ ]] || {
  echo "Commit must be the full 40-character Git SHA." >&2
  exit 2
}

case "$rid" in
  win-x64|win-arm64)
    executable="Heresy.exe"
    extension="zip"
    ;;
  linux-x64|linux-arm64)
    executable="Heresy"
    extension="tar.bz2"
    ;;
  osx-x64|osx-arm64)
    executable="Heresy"
    extension="zip"
    ;;
  *)
    echo "Unsupported release runtime: $rid" >&2
    exit 2
    ;;
esac

test -f "$publish_dir/$executable" || {
  echo "Missing published apphost: $executable" >&2
  exit 1
}
for assembly in Heresy Heresy.Core Heresy.Scripting Heresy.Playback \
  Heresy.Render Heresy.Render.SDL Heresy.Render.File Heresy.UserInterface; do
  test -f "$publish_dir/$assembly.dll" || {
    echo "Missing published assembly: $assembly.dll" >&2
    exit 1
  }
done
test -f "$repository/release/README.md"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/Heresy"
cp -a "$publish_dir/." "$stage/Heresy/"
cp "$repository/release/README.md" "$stage/Heresy/README.md"
printf '%s\n' "$commit" > "$stage/Heresy/BUILD-COMMIT.txt"

filename="Heresy-${version}-${rid}.${extension}"
archive="$output_dir/$filename"
if [[ "$extension" == "zip" ]]; then
  (cd "$stage" && zip -q -r -y "$archive" Heresy)
else
  tar -cjf "$archive" -C "$stage" Heresy
fi
test -s "$archive"
printf 'Created %s\n' "$archive"
