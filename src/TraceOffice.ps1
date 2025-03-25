<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture / View an ETW trace and other logs from Office apps: Word, Excel, PowerPoint, OneNote
	See: https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/collect-office-diagnostic-logs

	.DESCRIPTION

	TraceOffice Start [-All] [Start_Options] [-Shh]
	TraceOffice Stop  [-WPA [-FastSym]] [-Shh]
	TraceOffice View  [-Path <path>\MSO-Trace-Office.etl | <path>\<Name>.zip] [-FastSym]
	TraceOffice Status
	TraceOffice Cancel

	  -All :  Capture extra ETW providers (network activity) and extra logging sources.
	  -Shh :  Suppress explanatory output.
	  -WPA :  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	  -Path: Optional path to a previously collected trace.
	  -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	            See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	  -Verbose

	Start_Options
	  -Loop: Record only the last few minutes of activity (circular memory buffer).
	  -CLR : Resolve call stacks for C# (Common Language Runtime).
	  -JS  : Resolve call stacks for JavaScript.

	.LINK

	https://github.com/microsoft/MSO-Scripts/wiki/Trace-Office-Apps
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows
	https://learn.microsoft.com/en-us/shows/defrag-tools/39-windows-performance-toolkit
#>

[CmdletBinding(DefaultParameterSetName = "Stop")]
Param(
	# Start, Stop, Status, Cancel, View
	[Parameter(Position=0)]
	[string]$Command,

	# Capture extra ETW providers (network activity) and extra logging sources.
	[Parameter(ParameterSetName="Start")]
	[Switch]$All,

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

	# "Faster symbol resolution by loading only from SymCache, not PDB"
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$FastSym,

	# Suppress explanatory output
	[Parameter(ParameterSetName="Start")]
	[Parameter(ParameterSetName="Stop")]
	[switch]$Shh,

	# Optional path to a previously collected trace: MSO-Trace-Office.etl OR <path>\<Name>.zip
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null

	# [switch]$Verbose # implicit
)

# ===== CUSTOMIZE THIS =====

	# Apps to track: Word, Excel, PowerPoint, OneNote
	# These are for UI and also correspond to certain registry keys. See: SetLoggedOnRegistry_Office
	$script:OfficeAppList = "Word","Excel","PowerPoint","OneNote"

	# Processes to track: WinWord.exe, Excel.exe, PowerPnt.exe, OneNote.exe
	$script:OfficeProcessList = "WinWord","Excel","PowerPnt","OneNote"

	# Control the maximum size of Office Diagnostic log files when this script is running via these two values:

	[int]$script:MaxSize = 400 # 100-4900 MB total logging size permitted
	[int]$script:FileSize = 20 # Size in MB of individual files


	$TraceParams =
	@{
		RecordingProfiles =
		@(
			".\WPRP\CPU.wprp!CPU-Dispatch"
			".\WPRP\FileDiskIO.wprp!FileIO"
			".\WPRP\OfficeProviders.wprp!OfficeLogging"
			".\WPRP\WindowsProviders.wprp!Basic"
			".\WPRP\Handles.wprp!AllHandles"
			".\WPRP\Defender.wprp!AntiMalware.Light"

		<#	^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ReadMe.txt

			"Registry" # Built-in
			".\WPRP\FileDiskIO.wprp!FileIO"
			"c:\MyProfiles\MyRecordingProfile.wprp!CustomProfile"

			Other recording profiles can be added via the WPT_WPRP environment variable.
			$Env:WPT_WPRP="c:\path\MyProfile.wprp!ProfileName;c:\path\MyProfile2.wprp!ProfileName2"
			set WPT_WPRP=c:\path\MyProfile.wprp!ProfileName;c:\path\MyProfile2.wprp!ProfileName2

			Other recording providers can be added via the WPT_XPERF environment variable (in "XPerf -ON" format).
			$Env:WPT_XPERF="GUIDorNAME1 + GUIDorNAME2:::Stack + GUIDorNAME3:KeywordFlags:Level + ..."
			set WPT_XPERF=GUIDorNAME1 + GUIDorNAME2:::Stack + GUIDorNAME3:KeywordFlags:Level + ...

			See: https://github.com/microsoft/MSO-Scripts/wiki/Customize-Tracing#envvar
		#>
		)

		ProviderManifests =
		@(
			# Optional: Register Office ETW Provider Manifests not registered by default.
			# See: .\OETW\ReadMe.txt
			".\OETW\MsoEtwCM.man" # Office Code Markers
			".\OETW\MsoEtwAS.man" # AirSpace
		)

		# This is the arbitrary name of the tracing session/instance:
		InstanceName = "MSO-Trace-Office"

	} # $TraceParams

	if ($All)
	{
		# $All is only valid for: TraceOffice Start -All
		# Add the necessary profiles and manifests for: TraceNetwork View -Path <path>\MSO-Trace-Office.etl

		$TraceParams.RecordingProfiles +=
		@(
			".\WPRP\Network.wprp!NetworkMain" # or Network.15002.wprp - See ReadMe.txt
			".\WPRP\ThreadPool.wprp!ThreadPool" # or ThreadPool.15002.wprp - See ReadMe.txt
		)

		$TraceParams.ProviderManifests +=
		@(
			# See: .\OETW\ReadMe.txt
			".\OETW\MsoEtwTP.man"
			".\OETW\MsoEtwDQ.man"
		)
	}


	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\Defender.wpaProfile", ".\WPAP\Handles.wpaProfile", ".\WPAP\FileIO.wpaProfile", ".\WPAP\CPU.wpaProfile"

		# The default trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}


	$script:RegOffice16 = 'HKCU:\Software\Microsoft\Office\16.0'

	$ExtraLogs =
	@{
	<#
		Here add extra logging parameters NOT part of standard Office Diagnostic Logging, described here:
		https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/collect-office-diagnostic-logs

		See the comments at the top of INCLUDE.Office.ps1

		Herein put all registry and filepath strings in 'single-quotes'.
		$Env:Variables and $script:Variables will be resolved later.
	#>
		Registry =
		@(
			# Temporarily enable logging using these registry values.
			# Format: "KeyPath!ValueName=Value" where Value = string or number (REG_SZ or REG_DWORD automatically chosen)

			# General Office
			'$script:RegOffice16\Common\Logging!LogPath=$Env:TRACE_PATH\Diagnostics'
			'$script:RegOffice16\Common\Logging!EnableLogging=1'

			# Presence
			'$script:RegOffice16\Common\General!EnablePCXLogging=1'

			# PowerPoint
			'$script:RegOffice16\PowerPoint\Options!PPTActionPerfLog=$Env:TRACE_PATH\Diagnostics\PPTActionPerf.log'
		)

	<#
		Env-vars for file paths chosen by the application may be different between the app (Logged-on User?) and this running script (Administrator?).
		Therefore the script will duplicate entries containing environment path variables using both versions of the file path (if different).
		This applies exclusively to: $Env:UserData, $Env:LocalAppData, $Env:Temp
	#>
		Files =
		@(
			# Collect recent log files from these paths (wildcard filenames allowed).

			# General Office - Path defined above by !LogPath
			'$Env:TRACE_PATH\Diagnostics\$Env:COMPUTERNAME*.log' # ComputerName-Date-Time.log

			# Presence - Path chosen by the app.
			'$Env:LocalAppData\Microsoft\Office\16.0\PCX\Tracing\PCXTracing-*.etl'

			# PowerPoint - Path defined above by !PPTActionPerfLog
			'$Env:TRACE_PATH\Diagnostics\PPTActionPerf.log'
		)


		NewFolders =
		@(
			# These folders must be created in advance for the log files.

			# General Office and PowerPoint, as defined above
			'$Env:TRACE_PATH\Diagnostics'
		)

	} # $ExtraLogs

# ===== END CUSTOMIZE ====


if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = $PSScriptRoot
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"
. "$ScriptRootPath\INCLUDE.Office.ps1"


<#
	Copy all files newer than a certain date-time
	From:	$Env:Temp\Diagnostics\AppName\*
	Or:	$Env:TempAlt\Diagnostics\AppName\*
	To:	$Env:LocalAppDataAlt\MSO-Scripts\TraceName\<Date-Time>\...

	Likewise copy all the customizable/extra logs.

	See: https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/collect-office-diagnostic-logs
#>
function GatherLogs
{
Param (
	[string[]]$ExtraPaths,
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
	}

	# List paths of Extra logs to gather.

	foreach ($SearchPath in $ExtraPaths)
	{
		$Paths += $ExecutionContext.InvokeCommand.ExpandString($SearchPath)
	}

	# If the apps _might_ be running in a different user context then also list those log paths.

	if ($Env:TempAlt -ne $Env:Temp)
	{
		foreach ($Process in $script:OfficeProcessList)
		{
			$Paths += "$Env:TempAlt\Diagnostics\$Process\*"
		}

		foreach ($SearchPath in $ExtraPaths)
		{
			$AltPath = ResolveAltPath $SearchPath
			if ($AltPath) { $Paths += $AltPath }
		}
	}

	[string[]] $Logs = $Null

	foreach ($Path in $Paths)
	{
		$LogsT = Get-ChildItem -Path $Path -Recurse -File -ErrorAction:SilentlyContinue | Where-Object { $_.LastWriteTime -ge $StartTime }
		Write-Status ($LogsT | Measure-Object).Count "log files found:" $Path
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
		$ProcessNames = CheckProcessListState $script:OfficeProcessList $Null

		if ($ProcessNames)
		{
			if ($ProcessNames -is [array])
			{
				Write-Warn "These processes are already running, and their logs may not be generated: $ProcessNames"
			}
			else
			{
				Write-Warn $ProcessNames "is already running, and its logs may not be generated."
			}
			Write-Warn "To cancel tracing, run: $(GetScriptCommand) Cancel"
		}		

		# Log a start time here before any log files are created. We'll use it later.
		# This will also get noticed by any status call to: ListRunningProfiles
		SetProfileStartTime $TraceParams.InstanceName

		EnableLogging_Office $True

		if ($All)
		{
			EnableExtraLogging $True $ExtraLogs.Registry $ExtraLogs.NewFolders
		}

		return [ResultValue]::Started
		}

	"Stop"
		{
		EnableLogging_Office $False

		EnableExtraLogging $False $ExtraLogs.Registry $Null

		$StartTime = GetProfileStartDateTime $TraceParams.InstanceName

		if (!$StartTime)
		{
			Write-Warn "`tOffice logging is already stopped."
			return [ResultValue]::Success # Already stopped, no problem.
		}

		$ProcessNames = CheckProcessListState $script:OfficeProcessList $StartTime

		# Require Office apps to quit, or not?
		# If the app is still running, we can't collect the log file (sharing error).

		# if ($ProcessNames) { Write-Warn "`tCollecting logs anyway." }

		if ($ProcessNames)
		{
			Write-Action "Please quit $ProcessNames and run: $(GetScriptCommand) Stop"
			Write-Action "Or to cancel tracing, run: $(GetScriptCommand) Cancel"
			return [ResultValue]::Error
		}

		GatherLogs $ExtraLogs.Files -Shh:$Shh

		ClearProfileStartTime $TraceParams.InstanceName

		return [ResultValue]::Collected
		}

	"Cancel"
		{
		EnableLogging_Office $False

		EnableExtraLogging $False $ExtraLogs.Registry $Null

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

		WriteExtraLoggingStatus $ExtraLogs.Registry

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

	# Check PreWin10 now, Admin Privilege later.
	CheckPrerequisites -NoAdminCheck

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
		# $WPA switch
		break
	}
	View
	{
		if (!$ViewerParams.TraceFilePath) { $ViewerParams.TraceFilePath = GetRecentTraceFilePath "Office" $ViewerParams.TraceName }
		$WPA = $True
		break
	}
	Success
	{
		$WPA = $False
		break
	}
	Error
	{
		# ProcessTraceCommand failed.
		$Result = ProcessOfficeTraceCommand "Cancel"
		exit 1
	}
	}

	if ($WPA) { LaunchViewer @ViewerParams -FastSym:$FastSym }

exit 0 # Success
