param([string]$Keys = "^k", [int]$Wait = 400)
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.SendKeys]::SendWait($Keys)
Start-Sleep -Milliseconds $Wait
"sent: $Keys"
