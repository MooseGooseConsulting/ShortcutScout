Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class MC {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
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
[void][MC]::SetProcessDPIAware()

function Luma($x0, $y0, $w, $h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x0, $y0, 0, 0, $bmp.Size)
    $sum = 0.0; $n = 0
    for ($x = 0; $x -lt $w; $x += 3) {
        for ($y = 0; $y -lt $h; $y += 3) {
            $c = $bmp.GetPixel($x, $y)
            $sum += (0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B)
            $n++
        }
    }
    $g.Dispose(); $bmp.Dispose()
    return [math]::Round($sum / [math]::Max($n, 1), 2)
}

$proc = Get-Process ShortcutScout -ErrorAction Stop
$h = [MC]::Find([uint32]$proc.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_OVERLAY_WINDOW"; exit 1 }

$r = New-Object MC+RECT
[void][MC]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top

# Sample a clean slab inside the panel (title bar area, no rows) and the
# background strip immediately to the right of the panel.
$inside  = Luma ($r.Left + 60) ($r.Top + 30) 400 40
$outside = Luma ($r.Right + 20) ($r.Top + 30) 400 40

Write-Output ("RECT     x={0} y={1} w={2} h={3}" -f $r.Left, $r.Top, $w, $hh)
Write-Output ("LUMA inside={0}  outside={1}" -f $inside, $outside)
Write-Output ("DARKENED {0}  (ratio {1})" -f ($inside -lt $outside), [math]::Round($inside / [math]::Max($outside, 1), 3))
