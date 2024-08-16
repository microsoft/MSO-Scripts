@echo off
setlocal

REM The powershell script has the same path and base name as this batch script.
set _CMD=-file "%~dpn0.ps1" %*
echo PowerShell %_CMD%

REM Set a temporary Bypass execution policy in Process scope to run the PowerShell script without interruption.
REM https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies?#powershell-execution-policies
PowerShell -EP Unrestricted %_CMD%
