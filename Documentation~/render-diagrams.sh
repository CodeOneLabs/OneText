#!/usr/bin/env bash
# Renders every diagrams/*.mmd under Documentation~ to a PNG next to it.
#
#   ./Documentation~/render-diagrams.sh            # all diagrams
#   ./Documentation~/render-diagrams.sh Runtime/Core/Layout   # one module
#   ./Documentation~/render-diagrams.sh --changed  # only .mmd newer than their .png
#
# Needs Node.js. The first run lets npx fetch @mermaid-js/mermaid-cli, which
# pulls a headless Chromium through puppeteer (~150 MB). To reuse a browser you
# already have, point puppeteer at it:
#
#   echo '{ "executablePath": "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" }' > ~/.mmdc-puppeteer.json
#   PUPPETEER_CONFIG=~/.mmdc-puppeteer.json ./Documentation~/render-diagrams.sh
#
# MMDC overrides the command (e.g. MMDC=./node_modules/.bin/mmdc). If ImageMagick
# (`magick`) is on PATH the PNGs are palette-quantized afterwards; optional.
# The PNGs are committed, so a reader never needs this script — only an author
# who changed a .mmd does.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

mmdc="${MMDC:-npx -y @mermaid-js/mermaid-cli}"
scale="${SCALE:-2}"
changed=0
sub=""
for arg in "$@"; do
  case "$arg" in
    --changed) changed=1 ;;
    *) sub="$arg" ;;
  esac
done

puppeteer_args=()
if [[ -n "${PUPPETEER_CONFIG:-}" ]]; then
  puppeteer_args=(-p "$PUPPETEER_CONFIG")
fi

count=0
while IFS= read -r -d '' mmd; do
  png="${mmd%.mmd}.png"
  if [[ $changed -eq 1 && -f "$png" && ! "$mmd" -nt "$png" ]]; then
    continue
  fi
  echo "render  ${mmd#./}"
  # shellcheck disable=SC2086
  $mmdc "${puppeteer_args[@]}" -i "$mmd" -o "$png" -s "$scale" -b white --quiet
  # Diagrams are flat colour; a 256-colour palette is lossless to the eye and
  # about a third of the size. Skipped when ImageMagick is not installed.
  if command -v magick >/dev/null 2>&1; then
    magick "$png" -colors 256 -define png:compression-level=9 "$png"
  fi
  count=$((count + 1))
done < <(find "./${sub}" -path '*/diagrams/*.mmd' -print0 | sort -z)

echo "rendered $count diagram(s)"
