# Selects a navigation page in the running server window through UI Automation, optionally capturing it.
param([Parameter(Mandatory)][string]$Page, [string]$Out)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process RemoteControlServer -ErrorAction Stop | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::RadioButton)
$radios = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
foreach ($r in $radios) {
    if ($r.Current.Name -like "*$Page*") {
        $r.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 700
        if ($Out) { & "$PSScriptRoot\capture-window.ps1" -ProcessName RemoteControlServer -Out $Out -TitleLike 'PC Remote Center*' }
        return
    }
}
Write-Error "Page '$Page' not found"
