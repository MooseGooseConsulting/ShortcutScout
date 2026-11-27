param([int]$WaitSeconds = 34)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class ID {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  // Real injected input: SetCursorPos alone does not reset GetLastInputInfo,
  // which is why an earlier version of this script measured idle twice.
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
  public static void Nudge() { mouse_event(0x0001, 1, 0, 0, IntPtr.Zero); mouse_event(0x0001, -1, 0, 0, IntPtr.Zero); }
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
[void][ID]::SetProcessDPIAware()

$proc = Get-Process ShortcutScout -ErrorAction Stop
$h = [ID]::Find([uint32]$proc.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_OVERLAY_WINDOW"; exit 1 }

function Crop-And-Measure($tag) {
    $r = New-Object ID+RECT
    [void][ID]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left
    $hh = $r.Bottom - $r.Top
    $pad = 12
    $bmp = New-Object System.Drawing.Bitmap(($w + $pad*2), ($hh + $pad*2))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 32, 36, 44))
    $g.CopyFromScreen($r.Left - $pad, $r.Top - $pad, 0, 0, $bmp.Size)

    # Text-free band inside the card, so this measures the card itself.
    $sum = 0.0; $n = 0
    for ($x = [int]($w * 0.55); $x -lt $w - 30; $x += 4) {
        for ($y = [int]($hh * 0.55); $y -lt [int]($hh * 0.72); $y += 4) {
            $c = $bmp.GetPixel($x + $pad, $y + $pad)
            $sum += (0.2126*$c.R + 0.7152*$c.G + 0.0722*$c.B)
            $n++
        }
    }
    $out = Join-Path (Get-Location) "docs\screenshots\overlay-$tag.png"
    New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return [math]::Round($sum / [math]::Max($n,1), 2)
}

# Make sure we start from a genuinely ACTIVE state.
[ID]::Nudge()
Start-Sleep -Seconds 2
$active = Crop-And-Measure "active"
Write-Output ("ACTIVE  card_luma={0}  (input just injected, idle < 30s)" -f $active)

Write-Output ("WAITING {0}s untouched (threshold 30s)" -f $WaitSeconds)
Start-Sleep -Seconds $WaitSeconds

$idle = Crop-And-Measure "idle"
Write-Output ("IDLE    card_luma={0}  (no input for {1}s)" -f $idle, $WaitSeconds)
Write-Output ("BRIGHTER {0}  (delta {1})" -f ($idle -gt $active), [math]::Round($idle - $active, 2))
