# Three questions for a Windows runner, in the order that makes the next one
# worth asking. Everything is reported, nothing throws: a "no" here is an
# answer, not a failure.
$ErrorActionPreference = 'Continue'

function Section($name) { Write-Host ""; Write-Host "=== $name" }

Section "session"
Write-Host "OS      : $((Get-CimInstance Win32_OperatingSystem).Caption)"
Write-Host "Build   : $([System.Environment]::OSVersion.Version)"
Write-Host "User    : $env:USERNAME"
Write-Host "Interactive: $([System.Environment]::UserInteractive)"
try { (query session) | ForEach-Object { Write-Host "  $_" } } catch { Write-Host "  query session unavailable" }

Section "desktop"
# An IME needs a window station with a message queue. If a plain form cannot
# be shown, nothing further is possible and the rest of this is noise.
Add-Type -AssemblyName System.Windows.Forms
try {
    $f = New-Object System.Windows.Forms.Form
    $f.Text = 'probe'
    $f.Show()
    Start-Sleep -Milliseconds 300
    Write-Host "WINDOW: ok, handle $($f.Handle)"
    $f.Close()
} catch {
    Write-Host "WINDOW: FAILED - $_"
}

Section "input methods, before"
Get-WinUserLanguageList | ForEach-Object {
    Write-Host "  $($_.LanguageTag): $($_.InputMethodTips -join ', ')"
}

Section "install Korean"
if (Get-Command Install-Language -ErrorAction SilentlyContinue) {
    try { Install-Language ko-KR -ErrorAction Continue | Out-Null; Write-Host "  Install-Language ko-KR: returned" }
    catch { Write-Host "  Install-Language ko-KR: $_" }
} else {
    Write-Host "  Install-Language: cmdlet not present"
}
foreach ($cap in @('Language.Basic~~~ko-KR~0.0.1.0')) {
    try {
        $s = Get-WindowsCapability -Online -Name $cap -ErrorAction Stop
        Write-Host "  $cap -> $($s.State)"
        if ($s.State -ne 'Installed') {
            Add-WindowsCapability -Online -Name $cap -ErrorAction Continue | Out-Null
            Write-Host "  $cap -> $((Get-WindowsCapability -Online -Name $cap).State) after install"
        }
    } catch { Write-Host "  $cap : $_" }
}

Section "add ko-KR to the user language list"
try {
    $list = Get-WinUserLanguageList
    if (-not ($list | Where-Object { $_.LanguageTag -eq 'ko-KR' })) { $list.Add('ko-KR') }
    Set-WinUserLanguageList $list -Force
    Get-WinUserLanguageList | ForEach-Object {
        Write-Host "  $($_.LanguageTag): $($_.InputMethodTips -join ', ')"
    }
} catch { Write-Host "  $_" }

Section "keyboard layouts in this session"
$sig = @'
using System;
using System.Runtime.InteropServices;
public class Lay {
    [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int n, [Out] IntPtr[] h);
    [DllImport("user32.dll")] public static extern IntPtr LoadKeyboardLayout(string id, uint f);
}
'@
Add-Type -TypeDefinition $sig
$h = New-Object IntPtr[] 32
$n = [Lay]::GetKeyboardLayoutList(32, $h)
Write-Host "  loaded: $n"
for ($i = 0; $i -lt $n; $i++) { Write-Host ("  HKL {0:X8}" -f $h[$i].ToInt64()) }
$k = [Lay]::LoadKeyboardLayout('00000412', 1)
Write-Host ("  LoadKeyboardLayout(00000412) -> {0:X8}" -f $k.ToInt64())
Write-Host ""
Write-Host "=== done"

# A probe reports, it does not fail: native tools leave $LASTEXITCODE
# behind and the step wrapper exits with it.
exit 0
