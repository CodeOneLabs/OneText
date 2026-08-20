# Types A (U+C544), then backspace twice, into the OneText check player,
# using the Microsoft Korean IME and synthesised keystrokes. This is the
# question the Windows report asked, put to a machine instead of a person.
#
# This file stays ASCII on purpose: Windows PowerShell 5.1 reads a .ps1
# without a byte order mark as ANSI, and the Hangul that used to be in these
# comments came through as mojibake and broke the parser before a single key
# was sent.
#
# What makes the answer readable rather than merely a pass or a fail: the
# player logs the composition report and the field's value separately, with
# code points. If the report never moves the IME never engaged and the run
# says nothing about the fix - that is checked for explicitly below, because
# a silent "no Hangul" would otherwise read as a failure of the fix.
param(
    [Parameter(Mandatory = $true)][string]$Exe
)
$ErrorActionPreference = 'Continue'

$sig = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class Drv {
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1, pad2; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("imm32.dll")]  public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr LoadKeyboardLayout(string id, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    public const uint WM_IME_CONTROL = 0x0283;
    public const int  IMC_SETOPENSTATUS = 0x0006;
    public const int  IMC_GETOPENSTATUS = 0x0005;

    public static void Tap(ushort vk, ushort scan) { TapEx(vk, scan, 0); }

    public static void TapEx(ushort vk, ushort scan, uint extra) {
        INPUT[] a = new INPUT[2];
        a[0].type = 1; a[0].ki.wVk = vk; a[0].ki.wScan = scan; a[0].ki.dwFlags = extra;
        a[1].type = 1; a[1].ki.wVk = vk; a[1].ki.wScan = scan; a[1].ki.dwFlags = extra | 2;
        SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@
Add-Type -TypeDefinition $sig

$logPath = Join-Path (Split-Path -Parent $Exe) 'ime-check.log'
if (Test-Path $logPath) { Remove-Item $logPath -Force }

# The driver's way of clicking: one command written beside the executable,
# which the HUD obeys and deletes. Deletion is the acknowledgement.
$drivePath = Join-Path (Split-Path -Parent $Exe) 'ime-drive.txt'
if (Test-Path $drivePath) { Remove-Item $drivePath -Force }
function Send-Cmd([string]$cmd) {
    Set-Content -Path $drivePath -Value $cmd -Encoding Ascii
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 100
        if (-not (Test-Path $drivePath)) { return $true }
    }
    Write-Host "  (the player never consumed '$cmd')"
    return $false
}

Write-Host "launching $Exe"
$p = Start-Process -FilePath $Exe -ArgumentList '-imeAutoFocus', '-screen-fullscreen', '0', '-screen-width', '900', '-screen-height', '620' -PassThru

# The player needs to be up and its window real before anything is sent to it.
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.HasExited) { Write-Host "player exited early, code $($p.ExitCode)"; break }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
}
Write-Host ("window: {0:X}" -f $hwnd.ToInt64())
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host "RESULT: INCONCLUSIVE - the player never showed a window"
    if (Test-Path $logPath) { Get-Content $logPath | ForEach-Object { Write-Host "  $_" } }
    if (-not $p.HasExited) { $p.Kill() }
    exit 0
}

Start-Sleep -Seconds 3
[void][Drv]::ShowWindow($hwnd, 9)   # SW_RESTORE
[void][Drv]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 800
Write-Host ("foreground: {0:X}" -f ([Drv]::GetForegroundWindow()).ToInt64())

$hkl = [Drv]::LoadKeyboardLayout('00000412', 1)
[void][Drv]::PostMessage($hwnd, [Drv]::WM_INPUTLANGCHANGEREQUEST, [IntPtr]::Zero, $hkl)
Start-Sleep -Milliseconds 800

$imeWnd = [Drv]::ImmGetDefaultIMEWnd($hwnd)
Write-Host ("player IME window: {0:X}" -f $imeWnd.ToInt64())

function Read-Log {
    if (Test-Path $logPath) { Get-Content $logPath -Encoding UTF8 } else { @() }
}

# Has the composition report ever held Hangul? That, and not any return value
# from the IME, is what says the keystrokes are going through an input method.
# A syllable is U+AC00..U+D7A3, a lone jamo U+3130..U+318F or U+1100..U+11FF.
function Composed-Hangul {
    foreach ($line in (Read-Log)) {
        if ($line -notmatch '\[report\]') { continue }
        foreach ($ch in $line.ToCharArray()) {
            $c = [int]$ch
            if (($c -ge 0xAC00 -and $c -le 0xD7A3) -or
                ($c -ge 0x3130 -and $c -le 0x318F) -or
                ($c -ge 0x1100 -and $c -le 0x11FF)) { return $true }
        }
    }
    return $false
}

# Four ways to put the field into Hangul mode, cheapest first, each judged by
# whether a d actually composes rather than by what the call returned. The
# first one is the one that looked like it worked last time and did not: it
# reported open status 0 and the d landed in the value as an ASCII d.
$ways = @(
    @{ name = 'WM_IME_CONTROL IMC_SETOPENSTATUS'; act = {
        if ($imeWnd -ne [IntPtr]::Zero) {
            [void][Drv]::SendMessage($imeWnd, [Drv]::WM_IME_CONTROL, [IntPtr][Drv]::IMC_SETOPENSTATUS, [IntPtr]1)
            Write-Host ("  open status now: {0}" -f [Drv]::SendMessage($imeWnd, [Drv]::WM_IME_CONTROL, [IntPtr][Drv]::IMC_GETOPENSTATUS, [IntPtr]::Zero))
        }
    }},
    @{ name = 'VK_HANGUL'; act = { [Drv]::Tap(0x15, 0xF2) } },
    @{ name = 'right Alt (the Hangul key on a Korean keyboard)'; act = { [Drv]::TapEx(0xA5, 0x38, 0x0001) } },
    @{ name = 'VK_HANGUL with the right Alt scan code'; act = { [Drv]::TapEx(0x15, 0x38, 0x0001) } }
)

$composing = $false
foreach ($way in $ways) {
    Write-Host "--- trying: $($way.name)"
    & $way.act
    Start-Sleep -Milliseconds 700
    [Drv]::Tap(0x44, 0x20)          # d
    Start-Sleep -Milliseconds 900
    if (Composed-Hangul) { Write-Host "  composes"; $composing = $true; break }
    Write-Host "  no composition; clearing and trying the next"
    [Drv]::Tap(0x08, 0x0E)          # backspace, take the stray d back out
    Start-Sleep -Milliseconds 400
}

if (-not $composing) {
    Write-Host ""
    Write-Host "=== ime-check.log"
    Read-Log | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "RESULT: INCONCLUSIVE - no way of opening the IME made the player compose."
    Write-Host "        The keystrokes reach the field, so this is about the input method,"
    Write-Host "        not about the fix. Nothing here is evidence either way."
    if (-not $p.HasExited) { $p.Kill() }
    exit 0
}

# The d has already composed to the lone jamo. Add k to make the syllable,
# then the two presses the report is about, a beat apart so the field sees
# the report shrink on the way - the ordering the user described.
$before = (Read-Log).Count
Write-Host "--- k (composing to the full syllable)"; [Drv]::Tap(0x4B, 0x25); Start-Sleep -Milliseconds 900
Write-Host "--- backspace 1"; [Drv]::Tap(0x08, 0x0E); Start-Sleep -Milliseconds 1100
Write-Host "--- backspace 2"; [Drv]::Tap(0x08, 0x0E); Start-Sleep -Milliseconds 1800

# Scenario 2, off the 2026-08-21 report: the same jamo twice (d is the ieung
# key, and ieung cannot combine with itself), the focus taken away the way a
# click on empty canvas takes it, the focus given back, the same jamo twice
# more. Four presses, and all four have to reach the screen.
Write-Host "--- scenario 2: the same jamo, focus away and back, the same jamo again"
[void](Send-Cmd 'mark scenario-2')
Write-Host "--- d"; [Drv]::Tap(0x44, 0x20); Start-Sleep -Milliseconds 900
Write-Host "--- d"; [Drv]::Tap(0x44, 0x20); Start-Sleep -Milliseconds 900
Write-Host "--- away"; [void](Send-Cmd 'away'); Start-Sleep -Milliseconds 1200
Write-Host "--- back"; [void](Send-Cmd 'back'); Start-Sleep -Milliseconds 1200
Write-Host "--- d"; [Drv]::Tap(0x44, 0x20); Start-Sleep -Milliseconds 900
Write-Host "--- d"; [Drv]::Tap(0x44, 0x20); Start-Sleep -Milliseconds 1500

# Scenario 3, the report that followed the fix for scenario 2: one press of
# backspace, which on the reporting platform arrives as a four-event volley,
# deleted two. One press must delete exactly one.
Write-Host "--- scenario 3: one backspace"
[void](Send-Cmd 'mark scenario-3')
[Drv]::Tap(0x08, 0x0E); Start-Sleep -Milliseconds 1800

Start-Sleep -Seconds 2
if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }
if (-not $p.HasExited) { $p.Kill() }
Start-Sleep -Seconds 1

$lines = Read-Log
Write-Host ""
Write-Host "=== ime-check.log"
$lines | ForEach-Object { Write-Host "  $_" }

# The marks the driver wrote cut the log into scenarios; each verdict below
# reads only its own cut. Without them (an old player build) the extra
# scenarios are inconclusive and the first one reads the whole log, which is
# what it always did.
$mark2 = $lines.Count; $mark3 = $lines.Count
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($mark2 -eq $lines.Count -and $lines[$i] -match '\[mark  \] scenario-2') { $mark2 = $i }
    elseif ($mark3 -eq $lines.Count -and $lines[$i] -match '\[mark  \] scenario-3') { $mark3 = $i }
}

function Count-Ieung([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return 0 }
    $n = 0
    foreach ($ch in $s.ToCharArray()) {
        $c = [int]$ch
        if ($c -eq 0x3147 -or $c -eq 0x110B) { $n++ }   # ieung, compatibility or conjoining
    }
    return $n
}

function Code-Points([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return '(empty)' }
    $out = @()
    foreach ($ch in $s.ToCharArray()) { $out += ('U+{0:X4}' -f [int]$ch) }
    return ($out -join ' ')
}

# What the screen held once the first $upTo lines had happened: the last
# value and the last composition the HUD reported. Both persist across the
# scenario marks, which is why each verdict subtracts the state at its start.
function Screen-State($lines, $upTo) {
    $value = ''; $comp = ''
    for ($i = 0; $i -lt $upTo; $i++) {
        if ($lines[$i] -match "\[value \] .* -> '([^']*)' \[") { $value = $matches[1] }
        if ($lines[$i] -match "\[report\] .* -> '([^']*)' \[") { $comp = $matches[1] }
    }
    return @{ value = $value; comp = $comp }
}

Write-Host ""
Write-Host "=== verdict"
# Only what happened after the composition started counts. The escalation
# above leaves an ASCII d in the value on any attempt that did not compose,
# and backspaces it out again; those value lines are housekeeping and reading
# the last one of them as the answer is how the previous run called a pass on
# a composition that never existed.
$firstHangul = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notmatch '\[report\]') { continue }
    foreach ($ch in $lines[$i].ToCharArray()) {
        $c = [int]$ch
        if (($c -ge 0xAC00 -and $c -le 0xD7A3) -or ($c -ge 0x3130 -and $c -le 0x318F) -or
            ($c -ge 0x1100 -and $c -le 0x11FF)) { $firstHangul = $i; break }
    }
    if ($firstHangul -ge 0) { break }
}
if ($firstHangul -lt 0) {
    Write-Host "RESULT: INCONCLUSIVE - nothing ever composed"
    exit 0
}
Write-Host "composition starts at log line $firstHangul"

$after = @()
for ($i = $firstHangul; $i -lt $mark2; $i++) {
    if ($lines[$i] -match '\[value \]') { $after += $lines[$i] }
}

# The report the fix answers: the composition must have shrunk to a prefix of
# itself and then emptied. If the IME did something else this run is not the
# case being tested.
$sawShrink = $false
for ($i = $firstHangul; $i -lt $mark2; $i++) {
    if ($lines[$i] -match "\[report\] '(.+)' \[.*\] -> '' \[") { $sawShrink = $true }
}
Write-Host "composition emptied: $sawShrink"

if ($after.Count -eq 0) {
    Write-Host "RESULT: PASS - the composition was deleted and the value was never written to"
    Write-Host "        No third press is needed: nothing came back as committed text."
} else {
    Write-Host "value changes after the composition started:"
    $after | ForEach-Object { Write-Host "  $_" }
    $last = $after | Select-Object -Last 1
    $final = ''
    if ($last -match "-> '([^']*)'") { $final = $matches[1] }
    foreach ($ch in $final.ToCharArray()) { Write-Host ("  left in the value: U+{0:X4}" -f [int]$ch) }
    if ([string]::IsNullOrEmpty($final)) {
        Write-Host "RESULT: PASS - the value ended empty"
    } else {
        Write-Host "RESULT: FAIL - a deleted composition came back as committed text"
    }
}

Write-Host ""
Write-Host "=== verdict, scenario 2 (four presses of the same jamo across a focus loss)"
if ($mark2 -ge $lines.Count) {
    Write-Host "RESULT: INCONCLUSIVE - the player never consumed the drive commands; this build predates them"
} else {
    $base = Screen-State $lines $mark2
    $s2   = Screen-State $lines $mark3
    $baseCount = (Count-Ieung $base.value) + (Count-Ieung $base.comp)
    $s2Count   = (Count-Ieung $s2.value) + (Count-Ieung $s2.comp)
    $gained = $s2Count - $baseCount
    Write-Host ("before: value {0} composing {1}" -f (Code-Points $base.value), (Code-Points $base.comp))
    Write-Host ("after : value {0} composing {1}" -f (Code-Points $s2.value), (Code-Points $s2.comp))

    $moved = $false
    for ($i = $mark2; $i -lt $mark3; $i++) {
        if ($lines[$i] -match '\[report\]|\[value \]') { $moved = $true; break }
    }
    if (-not $moved) {
        Write-Host "RESULT: INCONCLUSIVE - the IME composed nothing in this scenario"
    } elseif ($gained -eq 4) {
        Write-Host "RESULT: PASS - all four presses reached the screen"
    } else {
        Write-Host "RESULT: FAIL - four presses of the same jamo put $gained on the screen"
    }
}

Write-Host ""
Write-Host "=== verdict, scenario 3 (one backspace deletes one)"
if ($mark3 -ge $lines.Count) {
    Write-Host "RESULT: INCONCLUSIVE - the scenario was never driven"
} else {
    $s2end = Screen-State $lines $mark3
    $s3    = Screen-State $lines $lines.Count
    $removed = ((Count-Ieung $s2end.value) + (Count-Ieung $s2end.comp)) -
               ((Count-Ieung $s3.value) + (Count-Ieung $s3.comp))
    Write-Host ("before: value {0} composing {1}" -f (Code-Points $s2end.value), (Code-Points $s2end.comp))
    Write-Host ("after : value {0} composing {1}" -f (Code-Points $s3.value), (Code-Points $s3.comp))
    if ($removed -eq 1) {
        Write-Host "RESULT: PASS - one press deleted exactly one"
    } elseif ($removed -eq 0) {
        Write-Host "RESULT: FAIL - the press deleted nothing"
    } else {
        Write-Host "RESULT: FAIL - one press deleted $removed"
    }
}
exit 0
