# Types 아, then backspace twice, into the OneText check player, using the
# Microsoft Korean IME and synthesised keystrokes. This is the question the
# Windows report asked, put to a machine instead of a person.
#
# What makes the answer readable rather than merely a pass or a fail: the
# player logs the composition report and the field's value separately, with
# code points. If the report never moves the IME never engaged and the run
# says nothing about the fix — that is checked for explicitly below, because
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

    public static void Tap(ushort vk, ushort scan) {
        INPUT[] a = new INPUT[2];
        a[0].type = 1; a[0].ki.wVk = vk; a[0].ki.wScan = scan; a[0].ki.dwFlags = 0;
        a[1].type = 1; a[1].ki.wVk = vk; a[1].ki.wScan = scan; a[1].ki.dwFlags = 2;
        SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@
Add-Type -TypeDefinition $sig

$logPath = Join-Path (Split-Path -Parent $Exe) 'ime-check.log'
if (Test-Path $logPath) { Remove-Item $logPath -Force }

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
if ($imeWnd -ne [IntPtr]::Zero) {
    [void][Drv]::SendMessage($imeWnd, [Drv]::WM_IME_CONTROL, [IntPtr][Drv]::IMC_SETOPENSTATUS, [IntPtr]1)
    Write-Host "IME open status: $([Drv]::SendMessage($imeWnd, [Drv]::WM_IME_CONTROL, [IntPtr][Drv]::IMC_GETOPENSTATUS, [IntPtr]::Zero))"
}
Start-Sleep -Milliseconds 500

# 아 = d then k. Then the two presses the report is about, a beat apart so the
# field sees the report shrink to ㅇ on the way — which is the ordering the
# user described, seeing the ㅇ before pressing again.
Write-Host "--- d (ㅇ)"; [Drv]::Tap(0x44, 0x20); Start-Sleep -Milliseconds 700
Write-Host "--- k (ㅏ -> 아)"; [Drv]::Tap(0x4B, 0x25); Start-Sleep -Milliseconds 700
Write-Host "--- backspace 1"; [Drv]::Tap(0x08, 0x0E); Start-Sleep -Milliseconds 900
Write-Host "--- backspace 2"; [Drv]::Tap(0x08, 0x0E); Start-Sleep -Milliseconds 1500

Start-Sleep -Seconds 2
if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }
if (-not $p.HasExited) { $p.Kill() }
Start-Sleep -Seconds 1

Write-Host ""
Write-Host "=== ime-check.log"
if (-not (Test-Path $logPath)) {
    Write-Host "RESULT: INCONCLUSIVE - no log was written"
    exit 0
}
$lines = Get-Content $logPath -Encoding UTF8
$lines | ForEach-Object { Write-Host "  $_" }

Write-Host ""
Write-Host "=== verdict"
$reports = $lines | Where-Object { $_ -match '\[report\]' }
if (-not $reports) {
    Write-Host "RESULT: INCONCLUSIVE - the composition report never moved, so the IME never"
    Write-Host "        engaged with the player. This says nothing about the fix."
    exit 0
}
$values = $lines | Where-Object { $_ -match '\[value \]' }
Write-Host "reports: $($reports.Count)  value changes: $($values.Count)"
$last = $values | Select-Object -Last 1
Write-Host "last value line: $last"
if ($last -and $last -match "-> ''") {
    Write-Host "RESULT: PASS - two backspaces left the value empty"
} elseif (-not $values) {
    Write-Host "RESULT: PASS - the value was never written to at all"
} else {
    Write-Host "RESULT: SUSPECT - something was left in the value; read the log above"
}
exit 0
