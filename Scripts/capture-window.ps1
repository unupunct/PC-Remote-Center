# Captures ONE window (by process name) to a PNG using PrintWindow - never the rest of the desktop.
param([Parameter(Mandatory)][string]$ProcessName, [Parameter(Mandatory)][string]$Out, [string]$TitleLike = '*')
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class Win {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$pids = (Get-Process -Name $ProcessName -ErrorAction Stop).Id
$found = $null
[Win]::EnumWindows({ param($h, $l)
    $p = 0; [void][Win]::GetWindowThreadProcessId($h, [ref]$p)
    if ($pids -contains $p -and [Win]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256; [void][Win]::GetWindowText($h, $sb, 256)
        if ($sb.ToString() -like $TitleLike -and $sb.Length -gt 0 -and -not $script:found) { $script:found = $h }
    }
    $true }, [IntPtr]::Zero) | Out-Null
if (-not $found) { Write-Error "No visible window for $ProcessName"; exit 1 }
$r = New-Object Win+RECT; [void][Win]::GetWindowRect($found, [ref]$r)
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
[void][Win]::PrintWindow($found, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Host "Saved $Out (${w}x${h})"
