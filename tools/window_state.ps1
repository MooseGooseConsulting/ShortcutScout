# Reports every top-level window the overlay process owns, including hidden ones.
# EnumWindows covers invisible top-level windows too, which is what we need when the
# panel is "not running" from the user's point of view but the process still exists.
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinState {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int c);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int c);

  public static List<string> ForProcess(uint pid) {
    var found = new List<string>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid) return true;
      var title = new StringBuilder(256); GetWindowTextW(h, title, 256);
      var cls = new StringBuilder(256); GetClassNameW(h, cls, 256);
      RECT r; GetWindowRect(h, out r);
      long ex = GetWindowLongPtr(h, -20).ToInt64();
      bool shown = IsWindowVisible(h) != false;
      found.Add(string.Format(
        "hwnd=0x{0:X}  visible={1}  class={2}  title=\"{3}\"  rect=({4},{5})-({6},{7})  size={8}x{9}  exstyle=0x{10:X8}",
        h.ToInt64(), shown, cls, title, r.L, r.T, r.R, r.B, r.R - r.L, r.B - r.T, ex));
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

$proc = Get-Process ShortcutScout -ErrorAction SilentlyContinue
if (-not $proc) { Write-Output "NOT_RUNNING"; exit 0 }

foreach ($p in @($proc)) {
  Write-Output ("PID {0}  responding={1}  cpu={2:n1}s  threads={3}  started={4}" -f $p.Id, $p.Responding, $p.CPU, $p.Threads.Count, $p.StartTime)
  $wins = [WinState]::ForProcess([uint32]$p.Id)
  if ($wins.Count -eq 0) { Write-Output "  NO_TOP_LEVEL_WINDOWS (window was destroyed; process is a zombie)" }
  foreach ($w in $wins) { Write-Output ("  " + $w) }
}
