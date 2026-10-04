param(
  [Parameter(Mandatory=$true)][double]$X,
  [Parameter(Mandatory=$true)][double]$Y,
  [int]$Count = 1
)
Add-Type -AssemblyName System.Windows.Forms
# координаты экрана (моно/мульти) — задаются в виртуальных пикселях системы
for ($i = 0; $i -lt $Count; $i++) {
  [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$X, [int]$Y)
  Start-Sleep -Milliseconds 80
  $sig = @'
using System;
using System.Runtime.InteropServices;
public class M {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
'@
  if (-not ("M" -as [type])) { Add-Type -TypeDefinition $sig }
  [M]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)  # left down
  Start-Sleep -Milliseconds 60
  [M]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)  # left up
  Start-Sleep -Milliseconds 200
}
"clicked $X,$Y x$Count"
