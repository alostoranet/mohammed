# Run this script once in an elevated PowerShell window on the Windows server.
$ruleName = "ALOSTORA REST HOUSE - LAN TCP 8096"
$existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($existing) { Remove-NetFirewallRule -DisplayName $ruleName }
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8096 -Profile Private -RemoteAddress LocalSubnet
Write-Host "Allowed TCP 8096 from the local subnet on Private network profiles."
