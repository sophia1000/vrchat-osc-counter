param([string]$Helper, [string]$TestPlan, [string]$Marker)
function Start-Process {
    param($FilePath, $WorkingDirectory, $ArgumentList, $WindowStyle)
    @{ Executable = $FilePath; WorkingDirectory = $WorkingDirectory; Arguments = $ArgumentList; WindowStyle = $WindowStyle } |
        ConvertTo-Json | Set-Content -LiteralPath $Marker
}
. $Helper -PlanPath $TestPlan -Quiet
exit $LASTEXITCODE
