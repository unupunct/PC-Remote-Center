# Opens the CMD terminal window from the dashboard (UI Automation), types a command into it, and captures the window.
param([Parameter(Mandatory)][string]$Out, [string]$Command = 'echo hello from the PC window & hostname & ver')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Fg { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid); }
"@
$p = Get-Process RemoteControlServer | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$nav = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::RadioButton))) | Where-Object { $_.Current.Name -like '*Terminal*' } | Select-Object -First 1
$nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 600
$btn = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'Show window')) | Select-Object -First 1
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 4
$fg = [Fg]::GetForegroundWindow(); $fpid = 0; [void][Fg]::GetWindowThreadProcessId($fg, [ref]$fpid)
if ($fpid -ne $p.Id) { Write-Error "Foreground window is not the server's terminal window (pid $fpid); not sending keys."; exit 2 }
[System.Windows.Forms.SendKeys]::SendWait(($Command -replace '[+^%~(){}\[\]]', '{$0}') + '{ENTER}')
Start-Sleep -Seconds 2
& "$PSScriptRoot\capture-window.ps1" -ProcessName RemoteControlServer -Out $Out -TitleLike '*Command Prompt*'
