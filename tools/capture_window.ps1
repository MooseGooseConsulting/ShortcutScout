# DPI-aware capture of a single top-level window, found by a title substring.
# The default PowerShell host is DPI-unaware on this machine (200% scaling), which makes
# GetWindowRect and CopyFromScreen disagree; SetProcessDPIAware removes the mismatch.
param(
  [Parameter(Mandatory = $true)][string]$TitleLike,
  [string]$Out = "docs\screenshots\window.png",
  # CopyFromScreen reads the framebuffer, so an occluded window captures as whatever is
  # on top of it. Raise it first when that matters.
  [switch]$BringToFront
)

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Cap {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int c);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

  public static void Raise(IntPtr h) {
    SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // TOPMOST, NOMOVE, NOSIZE, SHOWWINDOW
    SetForegroundWindow(h);
  }

  public static IntPtr Find(string needle) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256);
      GetWindowTextW(h, sb, 256);
      if (sb.ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) {
        found = h;
        return false;
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

[void][Cap]::SetProcessDPIAware()

$h = [Cap]::Find($TitleLike)
if ($h -eq [IntPtr]::Zero) { Write-Output "WINDOW_NOT_FOUND ($TitleLike)"; exit 1 }

if ($BringToFront) { [Cap]::Raise($h); Start-Sleep -Milliseconds 500 }

$r = New-Object Cap+RECT
[void][Cap]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L
$ht = $r.B - $r.T
Write-Output ("RECT {0},{1} {2}x{3}" -f $r.L, $r.T, $w, $ht)

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $ht))

# Absolute paths pass through; Join-Path would glue the working directory onto the front.
$full = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path (Get-Location) $Out }
New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "SAVED $full"
