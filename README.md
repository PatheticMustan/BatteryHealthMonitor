# BatteryHealthMonitor

![Screenshot of the script](demo.png)

Small powershell util that updates the charger status live.
You can get the same information from the following ps command:

```powershell
Get-CimInstance -Namespace root\wmi -ClassName BatteryStatus | Select PowerOnline, Charging, ChargeRate, DischargeRate
```

I built this because my charging brick's ID wire broke, so it would sometimes send the correct 240W and sometimes default to low power.
Ouch!

## Building

```powershell
dotnet build -c Release
```

Output .exe will be `bin\Release\net48\BatteryMonitor.exe`.
