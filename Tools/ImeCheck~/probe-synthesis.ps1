# Can synthesised keystrokes reach an IME on this runner?
#
# The Unity player is the expensive half of the question, so ask the cheap
# half first with a WinForms text box: install the Korean IME, put the box in
# Hangul mode, send d and k, and see whether the box ends up holding 아. If a
# plain text box cannot compose, nothing about the Unity player would tell us
# anything either.
$ErrorActionPreference = 'Continue'

$sig = @'
using System;
using System.Runtime.InteropServices;

public class Ime {
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1, pad2; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("imm32.dll")]  public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr LoadKeyboardLayout(string id, uint f);
    [DllImport("user32.dll")] public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint f);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    public const uint WM_IME_CONTROL = 0x0283;
    public const int  IMC_SETOPENSTATUS = 0x0006;
    public const int  IMC_GETOPENSTATUS = 0x0005;

    // Scan codes, so the IME sees a keyboard and not a virtual key it might
    // hand straight through.
    public static void Tap(ushort vk, ushort scan) {
        INPUT[] a = new INPUT[2];
        a[0].type = 1; a[0].ki.wVk = vk; a[0].ki.wScan = scan; a[0].ki.dwFlags = 0;
        a[1].type = 1; a[1].ki.wVk = vk; a[1].ki.wScan = scan; a[1].ki.dwFlags = 2;
        SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@
Add-Type -TypeDefinition $sig
Add-Type -AssemblyName System.Windows.Forms

$hkl = [Ime]::LoadKeyboardLayout('00000412', 1)
Write-Host ("HKL: {0:X8}" -f $hkl.ToInt64())

$form = New-Object System.Windows.Forms.Form
$form.Text = 'ime-synthesis'
$form.TopMost = $true
$box = New-Object System.Windows.Forms.TextBox
$box.Dock = 'Fill'; $box.Multiline = $true
$form.Controls.Add($box)
$form.Show()
$box.Focus()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 500

[void][Ime]::SetForegroundWindow($form.Handle)
[void][Ime]::ActivateKeyboardLayout($hkl, 0)
[void][Ime]::PostMessage($form.Handle, [Ime]::WM_INPUTLANGCHANGEREQUEST, [IntPtr]::Zero, $hkl)
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 500

$imeWnd = [Ime]::ImmGetDefaultIMEWnd($form.Handle)
Write-Host ("default IME window: {0:X}" -f $imeWnd.ToInt64())
if ($imeWnd -ne [IntPtr]::Zero) {
    [void][Ime]::SendMessage($imeWnd, [Ime]::WM_IME_CONTROL, [IntPtr][Ime]::IMC_SETOPENSTATUS, [IntPtr]1)
    $open = [Ime]::SendMessage($imeWnd, [Ime]::WM_IME_CONTROL, [IntPtr][Ime]::IMC_GETOPENSTATUS, [IntPtr]::Zero)
    Write-Host "IME open status: $open"
}
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 300

function Pump($ms) {
    $end = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
}

# d = 0x44 scan 0x20 -> ㅇ ;  k = 0x4B scan 0x25 -> ㅏ ;  together 아
Write-Host "--- typing d"
[Ime]::Tap(0x44, 0x20); Pump 500
Write-Host "box after d: '$($box.Text)'"
Write-Host "--- typing k"
[Ime]::Tap(0x4B, 0x25); Pump 500
Write-Host "box after k: '$($box.Text)'"

foreach ($ch in $box.Text.ToCharArray()) { Write-Host ("  box code point U+{0:X4}" -f [int]$ch) }

# The composition itself does not live in .Text until it commits, so force it
# out with a space and look again.
Write-Host "--- space to commit"
[Ime]::Tap(0x20, 0x39); Pump 500
Write-Host "box after space: '$($box.Text)'"
foreach ($ch in $box.Text.ToCharArray()) { Write-Host ("  box code point U+{0:X4}" -f [int]$ch) }

# Syllables U+AC00..U+D7A3 and the compatibility jamo block U+3130..U+318F.
$hangul = $box.Text.ToCharArray() | Where-Object {
    ([int]$_ -ge 0xAC00 -and [int]$_ -le 0xD7A3) -or ([int]$_ -ge 0x3130 -and [int]$_ -le 0x318F)
}
if ($hangul) {
    Write-Host "RESULT: SYNTHESIS REACHES THE IME"
} else {
    Write-Host "RESULT: no Hangul produced"
}
$form.Close()

exit 0
