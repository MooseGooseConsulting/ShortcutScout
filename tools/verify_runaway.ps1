param([switch]$AllowCursorControl)

# This script moves the real mouse pointer / injects real input, which takes over the
# desktop of whoever is using the machine. It must never run as part of a normal build or
# verification pass; pass -AllowCursorControl when you deliberately want it to drive the
# pointer (ideally inside a disposable VM, never on a work desktop).
if (-not $AllowCursorControl) {
    Write-Output "REFUSED: this script moves the real mouse pointer."
    Write-Output "Re-run with -AllowCursorControl to approve it, and prefer a disposable VM."
    exit 2
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class CV {
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

  public static IntPtr FindOverlay(uint pid) {
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
$h = [CV]::FindOverlay([uint32]$proc.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_OVERLAY_WINDOW"; exit 1 }

$r = New-Object CV+RECT
[void][CV]::GetWindowRect($h, [ref]$r)
Write-Output ("BEFORE  x={0} y={1} w={2} h={3}" -f $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top))

$orig = New-Object CV+PT
[void][CV]::GetCursorPos([ref]$orig)

# Park the cursor dead centre of the overlay.
$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int](($r.Top + $r.Bottom) / 2)
[void][CV]::SetCursorPos($cx, $cy)
Write-Output ("CURSOR  moved to {0},{1} (overlay centre)" -f $cx, $cy)

# The app polls every 500ms; give it several ticks.
Start-Sleep -Milliseconds 2000

$r2 = New-Object CV+RECT
[void][CV]::GetWindowRect($h, [ref]$r2)
Write-Output ("AFTER   x={0} y={1} w={2} h={3}" -f $r2.Left, $r2.Top, ($r2.Right-$r2.Left), ($r2.Bottom-$r2.Top))

$moved = ($r2.Left -ne $r.Left) -or ($r2.Top -ne $r.Top)
Write-Output ("MOVED   {0}  (delta {1},{2})" -f $moved, ($r2.Left - $r.Left), ($r2.Top - $r.Top))

$inside = ($cx -ge $r2.Left) -and ($cx -le $r2.Right) -and ($cy -ge $r2.Top) -and ($cy -le $r2.Bottom)
Write-Output ("CURSOR_STILL_INSIDE_OVERLAY  {0}" -f $inside)

[void][CV]::SetCursorPos($orig.X, $orig.Y)
Write-Output ("CURSOR  restored to {0},{1}" -f $orig.X, $orig.Y)
