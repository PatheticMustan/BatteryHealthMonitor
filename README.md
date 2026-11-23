# BatteryHealthMonitor

![Screenshot of the script](demo.png)

Small powershell util that updates the charger status live.
You can get the same information from the following ps command:

```powershell
Get-CimInstance -Namespace root\wmi -ClassName BatteryStatus | Select PowerOnline, Charging, ChargeRate, DischargeRate
```
