#!/usr/bin/env bash
#
# Tier-3 build smoke test, iOS Simulator.
#
# Same three questions as the Android runner - did IL2CPP strip something the
# package needs, did the native binary make it into the app, did the shader
# survive - asked of the other toolchain. The simulator is the point: it needs
# no device, no provisioning profile and no signing identity, and the
# xcframework already ships an ios-arm64_x86_64-simulator slice, so the binary
# under test is a real one rather than a stub.
#
#   Tools/run_ios_smoke.sh [--keep-simulator] [--out <dir>]
#
# Exit 0 = PASS, 1 = FAIL, 2 = could not get far enough to judge.

set -euo pipefail

# ---------------------------------------------------------------- constants

UNITY="/Applications/Unity/Hub/Editor/6000.0.77f1/Unity.app/Contents/MacOS/Unity"
PROJECT="/Users/mac/Downloads/HappyTextDev"
PACKAGE_ROOT="/Users/mac/Downloads/OneText"
IOS_SUPPORT="/Applications/Unity/Hub/Editor/6000.0.77f1/PlaybackEngines/iOSSupport"

APP_ID="com.onetext.smoke"
SCHEME="Unity-iPhone"
MARKER="ONETEXT-SMOKE:"

RUN_TIMEOUT=180

# ------------------------------------------------------------------- arguments

# Outside the repository, for the reason spelled out in run_android_smoke.sh:
# the package is referenced by file path, so build output written under
# ${PACKAGE_ROOT} is package content, and a log file Unity appends to during a
# build turns into an infinite reimport loop.
OUT_DIR="${HOME}/Library/Caches/OneTextSmoke/IOS"
KEEP_SIMULATOR=0

while [ $# -gt 0 ]; do
    case "$1" in
        --keep-simulator) KEEP_SIMULATOR=1; shift ;;
        --out) OUT_DIR="$2"; shift 2 ;;
        -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

XCODE_PROJECT_DIR="${OUT_DIR}/Xcode"
DERIVED="${OUT_DIR}/DerivedData"
LOG="${OUT_DIR}/ios-smoke.log"
UNITY_LOG="${OUT_DIR}/unity-build.log"
XCODE_LOG="${OUT_DIR}/xcodebuild.log"
CONSOLE_LOG="${OUT_DIR}/simulator-console.txt"

mkdir -p "${OUT_DIR}"
: > "${LOG}"
exec > >(tee -a "${LOG}") 2>&1

say() { printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"; }
die() { say "ABORT: $*"; exit 2; }

# See the note in run_android_smoke.sh: Unity 6 keeps its lockfile in Temp/,
# not Library/, so a check against Library/UnityLockfile never fires.
check_project_free() {
    local held=0 lock
    for lock in "${PROJECT}/Temp/UnityLockfile" "${PROJECT}/Library/UnityLockfile"; do
        if [ -f "${lock}" ] && command -v lsof >/dev/null 2>&1 && lsof -- "${lock}" >/dev/null 2>&1; then
            say "REFUSING TO RUN: ${lock} is held by another process."
            lsof -- "${lock}" 2>/dev/null | sed 's/^/    /'
            held=1
        fi
    done

    if pgrep -f "Unity.app/Contents/MacOS/Unity.*-projectPath[ =]*${PROJECT}" >/dev/null 2>&1; then
        say "REFUSING TO RUN: a Unity process already has ${PROJECT} open:"
        pgrep -fl "Unity.app/Contents/MacOS/Unity.*-projectPath[ =]*${PROJECT}" | sed 's/^/    /'
        held=1
    fi

    if [ "${held}" = "1" ]; then
        say "Wait for it to finish, then run this script again."
        exit 2
    fi
}

WE_BOOTED_SIMULATOR=0
SIM_UDID=""

# Same rule as the Android runner: the harness project is shared with the
# editor test suites, so the settings this build rewrites get put back.
SETTINGS="${PROJECT}/ProjectSettings/ProjectSettings.asset"
SETTINGS_BACKUP=""

restore_settings() {
    if [ -n "${SETTINGS_BACKUP}" ] && [ -f "${SETTINGS_BACKUP}" ]; then
        if cmp -s "${SETTINGS_BACKUP}" "${SETTINGS}"; then
            say "ProjectSettings.asset unchanged"
        else
            cp "${SETTINGS_BACKUP}" "${SETTINGS}"
            say "restored ProjectSettings.asset (backup kept at ${SETTINGS_BACKUP})"
        fi
    fi
}

cleanup() {
    restore_settings
    if [ -n "${SIM_UDID}" ]; then
        xcrun simctl terminate "${SIM_UDID}" "${APP_ID}" >/dev/null 2>&1 || true
        if [ "${WE_BOOTED_SIMULATOR}" = "1" ] && [ "${KEEP_SIMULATOR}" = "0" ]; then
            say "shutting down the simulator this run started"
            xcrun simctl shutdown "${SIM_UDID}" >/dev/null 2>&1 || true
        fi
    fi
}
trap cleanup EXIT

# ------------------------------------------------------- Xcode precondition

# Everything below needs the full Xcode, not the Command Line Tools: xcodebuild
# with an iphonesimulator SDK, simctl, and a simulator runtime. Say so once,
# precisely, rather than failing three different ways further down.
XCODE_PATH="$(xcode-select -p 2>/dev/null || true)"
NEED_XCODE=0
case "${XCODE_PATH}" in
    *Xcode.app*) ;;
    *) NEED_XCODE=1 ;;
esac
if [ "${NEED_XCODE}" = "0" ] && ! xcrun simctl list runtimes >/dev/null 2>&1; then
    NEED_XCODE=1
fi

if [ "${NEED_XCODE}" = "1" ]; then
    say "PRECONDITION NOT MET: the full Xcode is required and is not active."
    say "xcode-select currently points at: ${XCODE_PATH:-<nothing>}"
    echo
    echo "  Install Xcode from the App Store, then:"
    echo "    sudo xcode-select -s /Applications/Xcode.app"
    echo "    xcodebuild -runFirstLaunch"
    echo "    install an iOS simulator runtime from Xcode Settings > Platforms"
    echo
    exit 2
fi

[ -x "${UNITY}" ]     || die "Unity not found at ${UNITY}"
[ -d "${PROJECT}" ]   || die "dev harness project not found at ${PROJECT}"
[ -d "${IOS_SUPPORT}" ] || die "iOS Build Support is not installed at ${IOS_SUPPORT}.
    Install it with: '/Applications/Unity Hub.app/Contents/MacOS/Unity Hub' -- --headless install-modules --version 6000.0.77f1 -m ios"

# An iOS runtime has to exist, or simctl will happily list zero usable devices.
if ! xcrun simctl list runtimes 2>/dev/null | grep -q "iOS"; then
    say "PRECONDITION NOT MET: no iOS simulator runtime is installed."
    echo "  Install one from Xcode Settings > Platforms, or:"
    echo "    xcodebuild -downloadPlatform iOS"
    exit 2
fi

check_project_free

# --------------------------------------------------- Unity -> Xcode project

say "generating the Xcode project into ${XCODE_PROJECT_DIR}"
rm -rf "${XCODE_PROJECT_DIR}"
mkdir -p "${XCODE_PROJECT_DIR}"

if [ -f "${SETTINGS}" ]; then
    SETTINGS_BACKUP="${OUT_DIR}/ProjectSettings.asset.orig"
    cp "${SETTINGS}" "${SETTINGS_BACKUP}"
    say "backed up ProjectSettings.asset"
fi

BUILD_START=$(date +%s)
set +e
"${UNITY}" \
    -batchmode -quit -nographics \
    -projectPath "${PROJECT}" \
    -buildTarget iOS \
    -executeMethod SmokeBuild.IOSSimulator \
    -smokeOut "${XCODE_PROJECT_DIR}" \
    -logFile "${UNITY_LOG}"
BUILD_STATUS=$?
set -e
say "Unity finished in $(( $(date +%s) - BUILD_START ))s"

if [ "${BUILD_STATUS}" -ne 0 ]; then
    say "Unity exited ${BUILD_STATUS}. Last errors:"
    grep -nE "^\[SmokeBuild\] FAILED|error CS|Error building|Exception:" "${UNITY_LOG}" | tail -30 | sed 's/^/    /' || true
    say "full build log: ${UNITY_LOG}"
    exit 2
fi

XCODEPROJ="${XCODE_PROJECT_DIR}/${SCHEME}.xcodeproj"
[ -d "${XCODEPROJ}" ] || die "Unity reported success but ${XCODEPROJ} is missing. See ${UNITY_LOG}"

# ---------------------------------------------------------------- xcodebuild

say "compiling for the simulator"
rm -rf "${DERIVED}"

# Unity's generated project does not always ship a shared .xcscheme, and
# -scheme fails outright when the scheme is missing. Ask the project what it
# actually has and address the target directly if there is no scheme.
xcodebuild -project "${XCODEPROJ}" -list > "${OUT_DIR}/xcodebuild-list.txt" 2>&1 || true
if awk '/Schemes:/{s=1;next} /^$/{s=0} s' "${OUT_DIR}/xcodebuild-list.txt" | grep -qx "[[:space:]]*${SCHEME}[[:space:]]*"; then
    SELECTOR=(-scheme "${SCHEME}")
    say "building scheme ${SCHEME}"
else
    SELECTOR=(-target "${SCHEME}")
    say "no shared scheme '${SCHEME}'; building the target directly"
fi

# Unity 6000.0.77f1 writes ARCHS = x86_64 into every simulator configuration of
# the project it generates, and PlayerSettings.SetArchitecture does not change
# it. That is an Intel-era assumption: the simulator on an Apple Silicon Mac is
# arm64, and installing an x86_64-only bundle onto it fails with "Failed to find
# matching arch for input file". Override it with the host architecture.
#
# The IL2CPP script phase inside the project reads $ARCHS and maps it to its own
# --architecture flag, so this override reaches the native compile too rather
# than only the Xcode link step.
SIM_ARCH="$(uname -m)"
say "forcing ARCHS=${SIM_ARCH} (Unity generates x86_64, which an Apple Silicon simulator cannot run)"

set +e
xcodebuild \
    -project "${XCODEPROJ}" \
    "${SELECTOR[@]}" \
    -configuration Release \
    -sdk iphonesimulator \
    -derivedDataPath "${DERIVED}" \
    -destination "generic/platform=iOS Simulator" \
    CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO \
    ARCHS="${SIM_ARCH}" VALID_ARCHS="${SIM_ARCH}" ONLY_ACTIVE_ARCH=NO \
    build > "${XCODE_LOG}" 2>&1
XCODE_STATUS=$?
set -e

if [ "${XCODE_STATUS}" -ne 0 ]; then
    say "xcodebuild failed (${XCODE_STATUS}). Last errors:"
    grep -E "error:|ld: |Undefined symbol" "${XCODE_LOG}" | tail -30 | sed 's/^/    /' || tail -40 "${XCODE_LOG}" | sed 's/^/    /'
    say "full xcodebuild log: ${XCODE_LOG}"
    exit 2
fi

APP_BUNDLE="$(find "${DERIVED}/Build/Products" -maxdepth 2 -name '*.app' -type d | head -1)"
[ -n "${APP_BUNDLE}" ] || die "xcodebuild succeeded but no .app appeared under ${DERIVED}/Build/Products"
say "built ${APP_BUNDLE}"

# ----------------------------------------------------------------- simulator

# Newest available iPhone: the highest runtime, then the highest-numbered
# iPhone on it. Anything already booted wins, so a run in a warm environment
# does not pay the boot cost twice.
SIM_UDID="$(xcrun simctl list devices available -j \
    | python3 -c '
import json, re, sys
data = json.load(sys.stdin)["devices"]
def runtime_key(name):
    nums = re.findall(r"(\d+)", name.rsplit(".", 1)[-1])
    return tuple(int(n) for n in nums) or (0,)
best = None
for runtime, devices in data.items():
    if "iOS" not in runtime:
        continue
    for d in devices:
        if not d.get("isAvailable") or "iPhone" not in d["name"]:
            continue
        booted = d.get("state") == "Booted"
        model = tuple(int(n) for n in re.findall(r"(\d+)", d["name"])) or (0,)
        key = (booted, runtime_key(runtime), model)
        if best is None or key > best[0]:
            best = (key, d["udid"], d["name"], runtime, booted)
if best:
    print(best[1])
' || true)"

[ -n "${SIM_UDID}" ] || die "no available iPhone simulator found. Install a runtime from Xcode Settings > Platforms."

SIM_STATE="$(xcrun simctl list devices -j | python3 -c '
import json, sys
udid = sys.argv[1]
for devices in json.load(sys.stdin)["devices"].values():
    for d in devices:
        if d["udid"] == udid:
            print(d["state"])
            sys.exit(0)
print("Unknown")
' "${SIM_UDID}")"

say "simulator ${SIM_UDID} (${SIM_STATE})"
if [ "${SIM_STATE}" != "Booted" ]; then
    say "booting it"
    xcrun simctl boot "${SIM_UDID}"
    WE_BOOTED_SIMULATOR=1
    xcrun simctl bootstatus "${SIM_UDID}" -b
fi

say "installing ${APP_ID}"
xcrun simctl uninstall "${SIM_UDID}" "${APP_ID}" >/dev/null 2>&1 || true
xcrun simctl install "${SIM_UDID}" "${APP_BUNDLE}"

: > "${CONSOLE_LOG}"
xcrun simctl spawn "${SIM_UDID}" log stream --style compact --level debug \
    --predicate "processImagePath CONTAINS \"${APP_ID}\" OR senderImagePath CONTAINS \"${APP_ID}\" OR eventMessage CONTAINS \"ONETEXT-SMOKE\"" \
    > "${CONSOLE_LOG}" 2>&1 &
LOGSTREAM_PID=$!
sleep 2

say "launching"
xcrun simctl launch --console-pty "${SIM_UDID}" "${APP_ID}" >> "${CONSOLE_LOG}" 2>&1 &
LAUNCH_PID=$!

say "waiting up to ${RUN_TIMEOUT}s for '${MARKER}'"
RUN_START=$(date +%s)
VERDICT=""
while :; do
    if grep -qF "${MARKER}" "${CONSOLE_LOG}" 2>/dev/null; then
        VERDICT="$(grep -F "${MARKER}" "${CONSOLE_LOG}" | head -1)"
        break
    fi
    [ $(( $(date +%s) - RUN_START )) -lt "${RUN_TIMEOUT}" ] || break
    sleep 2
done

SHOT="${OUT_DIR}/ios-smoke.png"
if xcrun simctl io "${SIM_UDID}" screenshot "${SHOT}" >/dev/null 2>&1 && [ -s "${SHOT}" ]; then
    say "screenshot: ${SHOT}"
else
    say "could not capture a screenshot"
    rm -f "${SHOT}"
fi

kill "${LAUNCH_PID}" "${LOGSTREAM_PID}" 2>/dev/null || true
wait "${LAUNCH_PID}" 2>/dev/null || true
wait "${LOGSTREAM_PID}" 2>/dev/null || true

# --------------------------------------------------------------- verdict

echo
say "--- what the app reported ---"
grep -E "ONETEXT-SMOKE" "${CONSOLE_LOG}" | sed 's/^/    /' || say "    (nothing)"
echo

if [ -z "${VERDICT}" ]; then
    say "FAIL: no '${MARKER}' line within ${RUN_TIMEOUT}s."
    say "Crash and native-library evidence:"
    grep -iE "DllNotFound|dyld|Library not loaded|Symbol not found|Crash|Exception" "${CONSOLE_LOG}" | tail -30 | sed 's/^/    /' || true
    say "full console log: ${CONSOLE_LOG}"
    exit 1
fi

case "${VERDICT}" in
    *"${MARKER} PASS"*)
        say "PASS - ${VERDICT#*${MARKER}}"
        say "log: ${LOG}"
        exit 0
        ;;
    *)
        say "FAIL - ${VERDICT#*${MARKER}}"
        say "full console log: ${CONSOLE_LOG}"
        exit 1
        ;;
esac
