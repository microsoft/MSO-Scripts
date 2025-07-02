@echo off
setlocal

REM The powershell script has the same path and base name as this batch script.
set _PATH="%~dpn0.ps1"
set _ARGS=%*

REM Detect, warn, and remove Mark of the Web.
(more < "%~dpn0.ps1:Zone.Identifier") >nul 2>nul && (
	echo Unblocking downloaded PowerShell script: %~n0.ps1
	echo https://github.com/microsoft/MSO-Scripts/wiki/Frequently-Asked-Questions#policy
	echo:>"%~dpn0.ps1:Zone.Identifier"
	powershell unblock-file '%~dpn0.ps1' 2>nul
	echo:
)

REM Escape spaces and quotes for use with: -command
set _CMD=-command %_PATH: =` %
if defined _ARGS set _CMD=%_CMD% %_ARGS:"='%
echo PowerShell %_CMD%

REM Set a temporary execution policy in Process scope to run the PowerShell script without interruption.
REM https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies?#powershell-execution-policies
PowerShell -EP Unrestricted %_CMD%
