param([string]$Out = "docs\screenshots\overlay.png")

Add-Type -AssemblyName System.Drawing

# The desktop is running at 200% scaling. Without declaring DPI awareness this
# process gets virtualized (halved) coordinates, so GetWindowRect and
# CopyFromScreen disagree and the crop lands on the wrong pixels.
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Dpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@ -Language CSharp
[void][Dpi]::SetProcessDPIAware()

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class OV {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }

  public static IntPtr Find(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h,l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        long ex = GetWindowLongPtr(h, -20).ToInt64();
        if ((ex & 0x00080000) != 0) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@ -Language CSharp

$proc = Get-Process ShortcutScout -ErrorAction Stop
$h = [OV]::Find([uint32]$proc.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_OVERLAY_WINDOW"; exit 1 }

$r = New-Object OV+RECT
[void][OV]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top
Write-Output ("RECT x={0} y={1} w={2} h={3}" -f $r.Left, $r.Top, $w, $hh)

$cur = New-Object OV+PT
[void][OV]::GetCursorPos([ref]$cur)
Write-Output ("CURSOR x={0} y={1}" -f $cur.X, $cur.Y)

$pad = 12
$bmp = New-Object System.Drawing.Bitmap(($w + $pad * 2), ($hh + $pad * 2))
$g = [System.Drawing.Graphics]::FromImage($bmp)
# Neutral mid-tone background behind the transparent window so the panel's own
# translucency is visible in the result instead of blending into a screenshot of itself.
$g.Clear([System.Drawing.Color]::FromArgb(255, 32, 36, 44))
$g.CopyFromScreen($r.Left - $pad, $r.Top - $pad, 0, 0, $bmp.Size)

$full = Join-Path (Get-Location) $Out
New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "SAVED $full"
