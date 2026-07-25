# Legacy wrapper: start NapCat as alt account. Prefer tools\start-napcat.ps1.
param(
    [string]$Uin = "2901884390",
    [string]$NapCatShell = ""
)

& "$PSScriptRoot\start-napcat.ps1" -Uin $Uin -NapCatShell $NapCatShell
