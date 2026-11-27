# Drives the cursor through a sweep of screen positions and records where the overlay
# lands after each move. Used to check the dodge uses the whole screen rather than
# mirroring the pointer into a single corner.
#
# Coordinates are given as fractions of the work area so the sweep is independent of DPI.

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class PS2 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

  public static IntPtr FindOverlay(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h,l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid) {
        long ex = GetWindowLongPtr(h, -20).ToInt64();
        if ((ex & 0x00080000) != 0) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@ -Language CSharp

[void][PS2]::SetProcessDPIAware()

$proc = Get-Process ShortcutScout -ErrorAction Stop
$h = [PS2]::FindOverlay([uint32]$proc.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_OVERLAY_WINDOW"; exit 1 }

$screenW = [PS2]::GetSystemMetrics(0)
$screenH = [PS2]::GetSystemMetrics(1)

$orig = New-Object PS2+PT
[void][PS2]::GetCursorPos([ref]$orig)

# Fractions of the screen: both corners and the middle band, so a corner-only strategy
# shows up as repeats.
$points = @(
  @(0.15, 0.15), @(0.85, 0.15), @(0.50, 0.50),
  @(0.15, 0.85), @(0.85, 0.85), @(0.75, 0.40),
  @(0.25, 0.60), @(0.60, 0.20)
)

$seen = @{}
foreach ($p in $points) {
  $x = [int]($screenW * $p[0]); $y = [int]($screenH * $p[1])
  [void][PS2]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 1300   # two ticks plus the move-away animation

  $r = New-Object PS2+RECT
  [void][PS2]::GetWindowRect($h, [ref]$r)
  $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top

  $covered = ($x -ge $r.Left) -and ($x -le $r.Right) -and ($y -ge $r.Top) -and ($y -le $r.Bottom)
  Write-Output ("cursor=({0,4},{1,4})  panel=({2,4},{3,4}) {4}x{5}  covers_cursor={6}" -f $x, $y, $r.Left, $r.Top, $w, $hh, $covered)
  $seen["$($r.Left),$($r.Top)"] = $true
}

[void][PS2]::SetCursorPos($orig.X, $orig.Y)
Write-Output ("DISTINCT_POSITIONS {0} of {1}" -f $seen.Count, $points.Count)
