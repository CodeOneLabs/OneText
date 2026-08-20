#!/usr/bin/env bash
# Builds the interactive Windows IME check. See Tools/ImeCheck~/README.md.
set -euo pipefail

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.0.77f1/Unity.app/Contents/MacOS/Unity}"
PACKAGE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-${HOME}/Library/Caches/OneTextImeCheck}"
PROJECT="${OUT_DIR}/Project"
PLAYER="${OUT_DIR}/Player/OneTextImeCheck.exe"

[ -x "${UNITY}" ] || { echo "no editor at ${UNITY}; set UNITY=" >&2; exit 1; }

mkdir -p "${PROJECT}/Assets/Editor" "${PROJECT}/Assets/ImeCheck" "${PROJECT}/Packages"
cat > "${PROJECT}/Packages/manifest.json" <<JSON
{
  "dependencies": {
    "com.onetext.core": "file:${PACKAGE}",
    "com.unity.ugui": "2.0.0",
    "com.unity.modules.imageconversion": "1.0.0",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.ui": "1.0.0",
    "com.unity.modules.uielements": "1.0.0",
    "com.unity.modules.unitywebrequest": "1.0.0"
  }
}
JSON

cp "${PACKAGE}/Tools/ImeCheck~/ImeCheckHud.cs" "${PROJECT}/Assets/ImeCheck/"
cp "${PACKAGE}/Tools/ImeCheck~/BuildStampValue.cs" "${PROJECT}/Assets/ImeCheck/"
cp "${PACKAGE}/Tools/ImeCheck~/ImeCheckBuild.cs" "${PROJECT}/Assets/Editor/"

# A Korean-capable font, or the sequences being checked draw as boxes. Any
# .ttf with Hangul will do; the packaged demo subset is the one always here.
FONT="${FONT:-${PACKAGE}/page~/demo/Build/../../..}"
if [ -z "${FONT_TTF:-}" ]; then
  FONT_TTF="$(find "${PACKAGE}" -name '*.ttf' -path '*Hangul*' -o -name 'Pretendard*.ttf' 2>/dev/null | head -1)"
fi
[ -n "${FONT_TTF:-}" ] || { echo "set FONT_TTF=/path/to/a/korean.ttf" >&2; exit 1; }
cp "${FONT_TTF}" "${PROJECT}/Assets/PretendardVariable.ttf"

STAMP="$(cd "${PACKAGE}" && git rev-parse --short HEAD)"
sed -i '' "s/COMMIT_PLACEHOLDER/${STAMP}/" "${PROJECT}/Assets/ImeCheck/BuildStampValue.cs"

echo "building ${STAMP} -> ${PLAYER}"
"${UNITY}" -batchmode -projectPath "${PROJECT}" \
    -executeMethod ImeCheckBuild.Windows -imeOut "${PLAYER}" \
    -logFile "${OUT_DIR}/build.log" || {
  grep -aE "^\[ImeCheckBuild\]|error CS|Exception:" "${OUT_DIR}/build.log" | tail -20
  exit 1
}
rm -rf "${OUT_DIR}/Player/OneTextImeCheck_BurstDebugInformation_DoNotShip"
( cd "${OUT_DIR}" && rm -f OneTextImeCheck-win64.zip && zip -qr OneTextImeCheck-win64.zip Player )
echo "ready: ${OUT_DIR}/OneTextImeCheck-win64.zip"
