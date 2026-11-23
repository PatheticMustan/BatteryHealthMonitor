try {
    Clear-Host
    [Console]::CursorVisible = $false
    $top = [Console]::CursorTop

    while ($true) {
        $b = Get-CimInstance -Namespace root\wmi -ClassName BatteryStatus | Select-Object -First 1

        [Console]::SetCursorPosition(0, $top)
        Write-Host ('PowerOnline   : {0,-12}' -f $b.PowerOnline)
        Write-Host ('Charging      : {0,-12}' -f $b.Charging)
        Write-Host ('ChargeRate    : {0,-12}' -f $b.ChargeRate)
        Write-Host ('DischargeRate : {0,-12}' -f $b.DischargeRate)
        Write-Host ('Updated       : {0:HH:mm:ss.fff}' -f (Get-Date))

        Start-Sleep -Milliseconds 200
    }
}
finally {
    [Console]::CursorVisible = $true
}