<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture / View an ETW trace and other logs from Office apps: Word, Excel, PowerPoint
	See: https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/collect-office-diagnostic-logs

	.DESCRIPTION

	.\TraceOffice Start [-Loop] [-CLR] [-JS] [-Shh]
	.\TraceOffice Stop [-WPA] [-Shh]
	.\TraceOffice View [-Path <path>\MSO-Trace-Office.etl]
	.\TraceOffice Status
	.\TraceOffice Cancel
	  -Loop: Record only the last few minutes of activity (circular memory buffer). 
	  -CLR:  Resolve call stacks for C# (Common Language Runtime).
	  -JS:   Resolve call stacks for JavaScript.
	  -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	  -Path: Optional path to a previously collected trace.
	  -Shh:  Suppress explanatory output.
	  -Verbose

	These scripts work with pre-Win10 versions of the Windows Performance Toolkit (WPT).

	.LINK

	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows
	https://learn.microsoft.com/en-us/shows/defrag-tools/39-windows-performance-toolkit
#>

[CmdletBinding(DefaultParameterSetName = "Start")]
Param(
	# Start, Stop, Status, Cancel, View
	[Parameter(Position=0)]
	[string]$Command,

	# Record only the last few minutes of activity (circular memory buffer).
	[Parameter(ParameterSetName="Start")]
	[switch]$Loop,

	# Support Common Language Runtime / C#
	[Parameter(ParameterSetName="Start")]
	[switch]$CLR,

	# Support JavaScript
	[Parameter(ParameterSetName="Start")]
	[switch]$JS,

	# Launch WPA after collecting the trace
	[Parameter(ParameterSetName="Stop")]
	[switch]$WPA,

	# Suppress explanatory output
	[Parameter(ParameterSetName="Start")]
	[Parameter(ParameterSetName="Stop")]
	[switch]$Shh,

	# All - for compatibility only
	[Parameter(ParameterSetName="Start")]
	[switch]$All,

	# Optional path to a previously collected trace: MSO-Trace-Office.etl
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null

	# [switch]$Verbose # implicit
)

if (!$Path) { $Path = $Null } # for PSv2

# ===== CUSTOMIZE THIS =====

	# Apps to track: Word, Excel, PowerPoint
	$script:OfficeAppList = "Word","Excel","PowerPoint"

	# Processes to track: WinWord.exe, Excel.exe, PowerPnt.exe
	$script:OfficeProcessList = "WinWord","Excel","PowerPnt"

	# Control the maximum size of Office Diagnostic log files when this script is running via these two values:
	[int]$script:MaxSize = 400 # 100-4900 MB total logging size permitted
	[int]$script:FileSize = 20 # Size in MB of individual files

	$TraceParams =
	@{
		RecordingProfiles =
		@(
			".\WPRP\CPU.wprp.Verbose"
			".\WPRP\FileDiskIO.wprp.Light"
			".\WPRP\OfficeProviders.wprp"
			".\WPRP\Defender.wprp.Light"

		<#	^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ReadMe.txt

			"Registry" # Built-in
			".\WPRP\FileDiskIO.wprp.Verbose"
			"c:\MyProfiles\MyRecordingProfile.wprp"
		#>
		)

		# This is the arbitrary name of the tracing session/instance:
		InstanceName = "MSO-Trace-Office"
	}

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\CPU-FileIO.wpaProfile"

		# The default trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

# ===== END CUSTOMIZE ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = Resolve-Path "$PSScriptRoot\.."
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"
. "$ScriptRootPath\INCLUDE.Office.ps1"


<#
	Copy all files newer than a certain date-time from:
		$Env:TempAlt\Diagnostics\AppName\*
	To:
		$Env:LocalAppDataAlt\MSO-Scripts\TraceName\<Date-Time>\...

	See: https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/collect-office-diagnostic-logs
#>
function GatherLogs
{
Param (
	[switch]$Shh
)
	EnsureTracePath # Sets TracePath, TempAlt, etc.

	$LogPath = "$script:TracePath\Office"

	$StartTime = GetProfileStartDateTime $TraceParams.InstanceName

	# Create a list of files to gather.
 
	[string[]] $Paths = $Null

	# List paths of Office logs to gather.

	foreach ($Process in $script:OfficeProcessList)
	{
		$Paths += "$Env:Temp\Diagnostics\$Process\*"
		$Paths += "$Env:Temp\Diagnostics\$Process\Additional\*"
	}

	# If the apps _might_ be running in a different user context then also list those log paths.

	if ($Env:TempAlt -and ($Env:Temp -ne $Env:TempAlt))
	{
		foreach ($Process in $script:OfficeProcessList)
		{
			$Paths += "$Env:TempAlt\Diagnostics\$Process\*"
			$Paths += "$Env:TempAlt\Diagnostics\$Process\Additional\*"
		}
	}

	[string[]] $Logs = $Null

	foreach ($Path in $Paths)
	{
		# Not in PSv2: -Recurse -File for Get-ChildItem
		$LogsT = Get-Item -Path $Path -Include "*.log" -ErrorAction:SilentlyContinue | Where-Object { $_.LastWriteTime -ge $StartTime }
		Write-Status ($LogsT | Measure-Object).Count "log files on path:" $Path
		if ($LogsT) { $Logs += $LogsT.FullName }
	}

	$Result = DoGatherLogs $StartTime $LogPath $Logs

	switch ($Result)
	{
	Collected
	{
		if (!$Shh) { Write-Msg "`nOffice logs are in this folder: $(GetEnvPath $script:TracePath)`n$script:TracePath`n" }
		break
	}
	Started
	{
		Write-Warn "No Office logs were collected. (An Office app may not have run.)"
		break
	}
	Error
	{
		# Failed to copy!
		Write-Msg "`nOffice logs are here:`n$($Logs | Out-String)"
		break
	}
	View { break } # "Collection of logs was not started..."
	}
}


<#
	If any Office apps are still running, ask to quit the app or cancel tracing.
	Ignore processes started before $StartTime, if provided.
#>
function CheckProcessState
{
Param (
	[Nullable[DateTime]]$StartTime,
	[switch]$Warn
)
	$ProcessNames = CheckProcessListState $script:OfficeProcessList $StartTime -Warn:$Warn

	if ($ProcessNames)
	{
		if ($Warn)
		{
			if ($StartTime)
			{
				# Tracing is supposed to stop.	
				Write-Action "Please quit $ProcessNames and run: $(GetScriptCommand) Stop"
				Write-Action "Or to cancel tracing, run: $(GetScriptCommand) Cancel"
			}
			else
			{
				# Tracing is supposed to start.
				if ($ProcessNames -is [array])
				{
					Write-Warn "These processes are already running, and their logs may not be generated: $ProcessNames"
				}
				else
				{
					Write-Warn $ProcessNames "is already running, and its logs may not be generated."
				}
				Write-Warn "If you prefer to cancel tracing, run: $(GetScriptCommand) Cancel"
			}
		}
		return $False
	}
	return $True
}


<#
	Control Office logging.
	Return one of: Success (no-op), Started, Collected, Error.
#>
function ProcessOfficeTraceCommand
{
Param (
	[string]$Command,
	[switch]$Shh
)
	switch ($Command)
	{
	"Start"
		{
		if (GetProfileStartDateTime $TraceParams.InstanceName)
		{
			Write-Warn "`tOffice logging is already started."
			Write-Warn "`tPlease exercise Microsoft Office:" $script:OfficeAppList
			Write-Warn "`tThen run: $(GetScriptCommand) Stop"
			Write-Warn "`tOr run: $(GetScriptCommand) Cancel"
			return [ResultValue]::Success # Already stopped, no problem.
		}

		# Warn of currently running processes.
		CheckProcessState $Null -Warn > $Null

		# Log a start time here before any log files are created. We'll use it later.
		# This will also get noticed by any status call to: ListRunningProfiles
		SetProfileStartTime $TraceParams.InstanceName

		EnableLogging_Office $True

		return [ResultValue]::Started
		}
	"Stop"
		{
		EnableLogging_Office $False

		$StartTime = GetProfileStartDateTime $TraceParams.InstanceName

		if (!$StartTime)
		{
			Write-Warn "`tOffice logging is already stopped."
			return [ResultValue]::Success # Already stopped, no problem.
		}

		# Wait for Office to quit, or not?
		# If we the app is still running, we can't collect the log file (sharing error).
		if (!(CheckProcessState $StartTime -Warn)) { return [ResultValue]::Error }
		# if (!(CheckProcessState $StartTime)) { Write-Warn "`tCollecting logs anyway." }

		GatherLogs -Shh:$Shh

		ClearProfileStartTime $TraceParams.InstanceName

		return [ResultValue]::Collected
		}
	"Cancel"
		{
		EnableLogging_Office $False

		ClearProfileStartTime $TraceParams.InstanceName

		return [ResultValue]::Success
		}
	"Status"
		{
		$StartTime = GetProfileStartDateTime $TraceParams.InstanceName
		if ($StartTime)
		{
			Write-Msg "`tOffice logging began at $StartTime"
		}
		else
		{
			Write-Msg "`tOffice logging has not started."
		}

		WriteLoggingStatus_Office

		WriteRecentLogPath "Office"

		return [ResultValue]::Success
		}
	"View"
		{
		return [ResultValue]::View
		}
	default
		{
		WriteUsage
		return [ResultValue]::Error
		}
	}
}


# Main

	# Handle log files produced by Office.

	$LogResult = ProcessOfficeTraceCommand $Command -Shh:$Shh
	ValidateResult "ProcessOfficeTraceCommand" $LogResult

	if ($LogResult -eq [ResultValue]::Error) { exit 1 }

	# Handle ETW trace file produced by WPR.

	$ETWTrace = $True

	if (CheckAdminPrivilege)
	{
		$TraceResult = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS
		ValidateResult "ProcessTraceCommand" $TraceResult
	}
	else
	{
		$TraceResult = $LogResult # One of: Success, Started, Collected
		$WPA = $False # No trace to view.
		$ETWTrace = $False

		if ($LogResult -eq [ResultValue]::Started)
		{
			Write-Warn "Administrator privileges are not enabled. Collecting a reduced set of logs."
			# It's REALLY better to get all of the data.
			Write-Action "If at all possible, please run: $(GetScriptCommand) Cancel"
			Write-Action "Then open a window or log in with Administrator privileges,"
			Write-Action "and run: $(GetScriptCommand) Start"
		}

		# Set up script variables
		CheckPrerequisites -NoAdminCheck
	}

	switch ($TraceResult)
	{
	Started
	{
		if (!$Shh)
		{
			if ($ETWTrace) { Write-Msg "Office logging is enabled, and ETW tracing has started." }
			Write-Msg "Exercise a Microsoft Office application now:" $script:OfficeAppList
			Write-Msg "Then quit the Office app and run: $(GetScriptCommand) Stop"
		}
		break
	}
	Collected
	{
		ArchiveGatheredLogs $TraceParams.InstanceName -Trace:$ETWTrace -Shh:$Shh
		if ($WPA) { LaunchViewer @ViewerParams }
		break
	}
	View
	{
		if (!$ViewerParams.TraceFilePath) { $ViewerParams.TraceFilePath = GetRecentTraceFilePath "Office" $ViewerParams.TraceName }
		LaunchViewer @ViewerParams
		break
	}
	Error
	{
		# ProcessTraceCommand failed.
		$Result = ProcessOfficeTraceCommand "Cancel"
		exit 1
	}
	}

exit 0 # Success
