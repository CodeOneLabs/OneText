#!/usr/bin/env bash
#
# Tier-3 build smoke test, Android.
#
# Builds a one-scene IL2CPP APK from the dev harness, runs it on the
# OneTextSmoke emulator, and reports on the single line the app prints. What it
# catches is everything the editor test suite structurally cannot: managed code
# the IL2CPP stripper removed, native libraries the Android packager left out,
# and shader variants that never made it into the build.
#
#   Tools/run_android_smoke.sh [--keep-emulator] [--out <dir>]
#
# Exit 0 = PASS, 1 = FAIL, 2 = could not get far enough to judge.

set -euo pipefail

# ---------------------------------------------------------------- constants

UNITY="/Applications/Unity/Hub/Editor/6000.0.77f1/Unity.app/Contents/MacOS/Unity"
PROJECT="/Users/mac/Downloads/HappyTextDev"
PACKAGE_ROOT="/Users/mac/Downloads/OneText"
ANDROID_PLAYER="/Applications/Unity/Hub/Editor/6000.0.77f1/PlaybackEngines/AndroidPlayer"
SDK="${ANDROID_PLAYER}/SDK"
ADB="${SDK}/platform-tools/adb"
EMULATOR="${SDK}/emulator/emulator"

AVD_NAME="OneTextSmoke"
APP_ID="com.onetext.smoke"
MARKER="ONETEXT-SMOKE:"

BOOT_TIMEOUT=300     # A cold first boot of this AVD measured 97s; 300 is slack.
RUN_TIMEOUT=180      # The app itself does its work in seconds; this is slack.

# A player that starts at all prints its verdict within about six seconds, so a
# minute of silence means it never got there. Cut the early attempts short and
# give the last one the full timeout.
LAUNCH_ATTEMPTS=4
FIRST_ATTEMPT_TIMEOUT=60

# ------------------------------------------------------------------- arguments

# Deliberately outside the repository. The package is referenced by file path,
# so anything written under ${PACKAGE_ROOT} is package content as far as the
# asset database is concerned - and Unity appends to the build log while the
# build runs, which reimports the package, which restarts the build, which
# Unity eventually reports as "an infinite import loop has been detected".
OUT_DIR="${HOME}/Library/Caches/OneTextSmoke/Android"
KEEP_EMULATOR=0

while [ $# -gt 0 ]; do
    case "$1" in
        --keep-emulator) KEEP_EMULATOR=1; shift ;;
        --out) OUT_DIR="$2"; shift 2 ;;
        -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

mkdir -p "${OUT_DIR}"
APK="${OUT_DIR}/OneTextSmoke.apk"
LOG="${OUT_DIR}/android-smoke.log"
UNITY_LOG="${OUT_DIR}/unity-build.log"
: > "${LOG}"

# Everything this script says goes to the console and to the log at once, so a
# failure a week from now is still readable.
exec > >(tee -a "${LOG}") 2>&1

say() { printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"; }
die() { say "ABORT: $*"; exit 2; }

# Unity is a single-writer: a second batch run against an open project aborts
# with "another Unity instance is running" and can leave the Library in a mess.
#
# Note the lockfile path. Unity 6 keeps it in Temp/, not Library/ - checking
# Library/UnityLockfile silently passes forever, which is exactly the bug this
# function was written to fix. The running-process check is the belt to that
# lockfile's braces, because the file only exists while the editor is live.
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

WE_BOOTED_EMULATOR=0
EMULATOR_PID=""

# The smoke build rewrites PlayerSettings - scripting backend, stripping level,
# architectures, graphics APIs, bundle id - and the harness project is also
# where the editor test suites run. Put the file back the way we found it.
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
    if [ "${WE_BOOTED_EMULATOR}" = "1" ] && [ "${KEEP_EMULATOR}" = "0" ]; then
        say "shutting down the emulator this run started"
        "${ADB}" ${ANDROID_SERIAL:+-s "${ANDROID_SERIAL}"} emu kill >/dev/null 2>&1 || true
        if [ -n "${EMULATOR_PID}" ]; then
            for _ in $(seq 1 20); do
                kill -0 "${EMULATOR_PID}" 2>/dev/null || break
                sleep 1
            done
            kill -9 "${EMULATOR_PID}" 2>/dev/null || true
        fi
    fi
}
trap cleanup EXIT

# ----------------------------------------------------------- preconditions

[ -x "${UNITY}" ]   || die "Unity not found at ${UNITY}"
[ -d "${PROJECT}" ] || die "dev harness project not found at ${PROJECT}"
[ -x "${ADB}" ]     || die "adb not found at ${ADB} - install Android Build Support"
[ -x "${EMULATOR}" ] || die "emulator not found at ${EMULATOR} - run: sdkmanager --sdk_root=${SDK} emulator"

check_project_free

export ANDROID_SDK_ROOT="${SDK}"
export ANDROID_HOME="${SDK}"
export ANDROID_AVD_HOME="${HOME}/.android/avd"
export JAVA_HOME="${ANDROID_PLAYER}/OpenJDK"

if ! "${EMULATOR}" -list-avds 2>/dev/null | grep -qx "${AVD_NAME}"; then
    die "AVD '${AVD_NAME}' does not exist. Create it with:
    avdmanager create avd -n ${AVD_NAME} -k 'system-images;android-36;google_apis;arm64-v8a' -d pixel_6"
fi

# ------------------------------------------------------------------- build

say "building ${APK}"
rm -f "${APK}"

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
    -buildTarget Android \
    -executeMethod SmokeBuild.Android \
    -smokeOut "${APK}" \
    -logFile "${UNITY_LOG}"
BUILD_STATUS=$?
set -e
BUILD_SECONDS=$(( $(date +%s) - BUILD_START ))

if [ "${BUILD_STATUS}" -ne 0 ]; then
    say "Unity exited ${BUILD_STATUS} after ${BUILD_SECONDS}s. Last errors:"
    grep -nE "^\[SmokeBuild\] FAILED|error CS|Error building|Exception:" "${UNITY_LOG}" | tail -30 | sed 's/^/    /' || true
    say "full build log: ${UNITY_LOG}"
    exit 2
fi
[ -f "${APK}" ] || { say "Unity reported success but ${APK} is missing. See ${UNITY_LOG}"; exit 2; }
say "built in ${BUILD_SECONDS}s ($(du -h "${APK}" | cut -f1))"

# ---------------------------------------------------------------- emulator

"${ADB}" start-server >/dev/null 2>&1 || true

online_device() {
    "${ADB}" devices | awk '$2 == "device" { print $1; exit }'
}

kill_emulator() {
    "${ADB}" emu kill >/dev/null 2>&1 || true
    if [ -n "${EMULATOR_PID}" ]; then
        for _ in $(seq 1 20); do
            kill -0 "${EMULATOR_PID}" 2>/dev/null || break
            sleep 1
        done
        kill -9 "${EMULATOR_PID}" 2>/dev/null || true
    fi
    EMULATOR_PID=""
    WE_BOOTED_EMULATOR=0
}

boot_emulator() {
    say "booting ${AVD_NAME} headless"
    "${EMULATOR}" -avd "${AVD_NAME}" \
        -no-window -no-audio -no-boot-anim -no-snapshot \
        -gpu swiftshader_indirect \
        > "${OUT_DIR}/emulator.log" 2>&1 &
    EMULATOR_PID=$!
    WE_BOOTED_EMULATOR=1

    local start elapsed booted
    start=$(date +%s)
    while :; do
        elapsed=$(( $(date +%s) - start ))
        [ "${elapsed}" -lt "${BOOT_TIMEOUT}" ] || { say "emulator did not boot within ${BOOT_TIMEOUT}s; see ${OUT_DIR}/emulator.log"; return 1; }
        kill -0 "${EMULATOR_PID}" 2>/dev/null || { say "emulator process died; see ${OUT_DIR}/emulator.log"; return 1; }

        DEVICE="$(online_device || true)"
        if [ -n "${DEVICE}" ]; then
            booted="$("${ADB}" -s "${DEVICE}" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r\n ')"
            [ "${booted}" = "1" ] && break
        fi
        sleep 3
    done
    say "booted in $(( $(date +%s) - start ))s"
    export ANDROID_SERIAL="${DEVICE}"
}

install_app() {
    say "installing ${APP_ID}"
    "${ADB}" -s "${DEVICE}" uninstall "${APP_ID}" >/dev/null 2>&1 || true
    "${ADB}" -s "${DEVICE}" install -r -g "${APK}" | sed 's/^/    /'
}

DEVICE="$(online_device || true)"
if [ -z "${DEVICE}" ]; then
    boot_emulator || exit 2
else
    say "using the device already attached: ${DEVICE}"
    export ANDROID_SERIAL="${DEVICE}"
fi

say "device: ${DEVICE} android $("${ADB}" -s "${DEVICE}" shell getprop ro.build.version.release | tr -d '\r\n') abi $("${ADB}" -s "${DEVICE}" shell getprop ro.product.cpu.abi | tr -d '\r\n')"

# ------------------------------------------------------------------- run

install_app

RESULT_LOG="${OUT_DIR}/logcat.txt"
VERDICT=""
LOGCAT_PID=""

# One launch, watched until the marker appears, the app dies, or time runs out.
# Sets VERDICT when the app reached a conclusion; leaves it empty otherwise.
attempt_run() {
    local attempt="$1" deadline="$2"

    "${ADB}" -s "${DEVICE}" shell am force-stop "${APP_ID}" >/dev/null 2>&1 || true
    "${ADB}" -s "${DEVICE}" logcat -c || true
    : > "${RESULT_LOG}"
    "${ADB}" -s "${DEVICE}" logcat -v time > "${RESULT_LOG}" 2>&1 &
    LOGCAT_PID=$!

    say "launch attempt ${attempt}: waiting up to ${deadline}s for '${MARKER}'"
    "${ADB}" -s "${DEVICE}" shell monkey -p "${APP_ID}" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1 \
        || die "could not launch ${APP_ID}"

    local start elapsed
    start=$(date +%s)
    while :; do
        if grep -qF "${MARKER}" "${RESULT_LOG}" 2>/dev/null; then
            VERDICT="$(grep -F "${MARKER}" "${RESULT_LOG}" | head -1)"
            return 0
        fi
        elapsed=$(( $(date +%s) - start ))
        [ "${elapsed}" -lt "${deadline}" ] || return 1

        # A process that died before printing anything is a crash, not a hang.
        if [ "${elapsed}" -gt 20 ] && ! "${ADB}" -s "${DEVICE}" shell pidof "${APP_ID}" >/dev/null 2>&1; then
            sleep 3   # last chance for buffered output to land
            if grep -qF "${MARKER}" "${RESULT_LOG}" 2>/dev/null; then
                VERDICT="$(grep -F "${MARKER}" "${RESULT_LOG}" | head -1)"
                return 0
            fi
            say "the app exited without printing the marker - it crashed on startup."
            return 1
        fi
        sleep 2
    done
}

# Unity's player deadlocks before a single line of script runs on roughly half
# of this emulator's cold boots: the game loop parks in pthread_cond_wait
# waiting for a graphics device, and no render thread is ever created. It is an
# engine/emulator race with nothing to do with OneText.
#
# Relaunching the app on the same emulator never recovers - once the graphics
# pipe is wedged it stays wedged, which is why three relaunches in a row all
# failed. A fresh emulator is what actually re-rolls the dice, so each retry
# gets one.
#
# This cannot turn a real failure into a pass: a player that starts at all
# prints its verdict either way, and only the "never reached script code" case
# is retried. A genuinely broken build just costs a few extra boots.
for ATTEMPT in $(seq 1 "${LAUNCH_ATTEMPTS}"); do
    DEADLINE="${RUN_TIMEOUT}"
    [ "${ATTEMPT}" -lt "${LAUNCH_ATTEMPTS}" ] && DEADLINE="${FIRST_ATTEMPT_TIMEOUT}"

    if attempt_run "${ATTEMPT}" "${DEADLINE}"; then
        break
    fi

    kill "${LOGCAT_PID}" 2>/dev/null || true
    wait "${LOGCAT_PID}" 2>/dev/null || true
    LOGCAT_PID=""

    if [ "${ATTEMPT}" -lt "${LAUNCH_ATTEMPTS}" ]; then
        cp "${RESULT_LOG}" "${OUT_DIR}/logcat-attempt-${ATTEMPT}.txt" 2>/dev/null || true
        say "no marker in ${DEADLINE}s - the player never reached script code."

        # Only worth rebooting the emulator if this run owns it.
        if [ "${WE_BOOTED_EMULATOR}" = "1" ]; then
            say "rebooting the emulator for attempt $(( ATTEMPT + 1 ))"
            kill_emulator
            sleep 3
            DEVICE=""
            boot_emulator || { say "could not reboot the emulator"; break; }
            install_app
        else
            say "relaunching on the attached device"
        fi
    fi
done

kill "${LOGCAT_PID}" 2>/dev/null || true
wait "${LOGCAT_PID}" 2>/dev/null || true

# Photograph the result before stopping the app. The scene deliberately stays
# on screen for a while after it prints the verdict, which is the window this
# uses.
SHOT="${OUT_DIR}/android-smoke.png"
if "${ADB}" -s "${DEVICE}" exec-out screencap -p > "${SHOT}" 2>/dev/null && [ -s "${SHOT}" ]; then
    say "screenshot: ${SHOT}"
else
    say "could not capture a screenshot"
    rm -f "${SHOT}"
fi

"${ADB}" -s "${DEVICE}" shell am force-stop "${APP_ID}" >/dev/null 2>&1 || true

# --------------------------------------------------------------- verdict

echo
say "--- what the app reported ---"
grep -E "ONETEXT-SMOKE" "${RESULT_LOG}" | sed 's/^/    /' || say "    (nothing)"
echo

if [ -z "${VERDICT}" ]; then
    say "FAIL: no '${MARKER}' line within ${RUN_TIMEOUT}s."
    say "Crash and native-library evidence from logcat:"
    grep -iE "DllNotFound|UnsatisfiedLink|FATAL EXCEPTION|libHarfBuzz|Unity  *: |beginning of crash" "${RESULT_LOG}" | tail -30 | sed 's/^/    /' || true
    say "full logcat: ${RESULT_LOG}"
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
        say "full logcat: ${RESULT_LOG}"
        exit 1
        ;;
esac
