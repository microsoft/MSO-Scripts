<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture / View an ETW trace and other logs from Outlook.
	See: https://support.microsoft.com/en-us/topic/how-to-enable-global-and-advanced-logging-for-microsoft-outlook-15c74560-2aaa-befd-c256-7c8823b1aefa

	.DESCRIPTION

	.\TraceOutlook Start [-Loop] [-CLR] [-JS] [-Shh]
	.\TraceOutlook Stop [-WPA] [-Shh]
	.\TraceOutlook View [-Path <path>\MSO-Trace-Outlook.etl]
	.\TraceOutlook Status
	.\TraceOutlook Cancel
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

	# Optional path to a previously collected trace: MSO-Trace-Outlook.etl
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null

	# [switch]$Verbose # implicit
)

if (!$Path) { $Path = $Null } # for PSv2

# ===== CUSTOMIZE THIS =====

	# Apps to track: Outlook
	# These are for UI and also correspond to certain registry keys. See: SetLoggedOnRegistry_Outlook
	$script:OfficeAppList = ,"Outlook"

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
		InstanceName = "MSO-Trace-Outlook"
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
	Create the logging registry key paths and values.
	If needed, switch them to the path of the logged-on user for Outlook.
	See: https://support.microsoft.com/en-us/topic/how-to-enable-global-and-advanced-logging-for-microsoft-outlook-15c74560-2aaa-befd-c256-7c8823b1aefa
#>
function SetLoggedOnRegistry
{
	if (Get-Variable -Name "RegOutlookOptions16" -Scope Script -ValueOnly -ErrorAction:SilentlyContinue) { return } # for strict mode

	$KeyPrefix = GetUserRegKeyPrefix

	# $script:RegOutlookOptions## = "HKCU:\Software\Microsoft\Office\##.0\Outlook\Options"
	# $script:RegPolicyOptions##  = "HKCU:\Software\Policies\Microsoft\Office\##.0\Outlook\Options"

	for ($Ver = 14; $Ver -le 16; $Ver += 1)
	{
		New-Variable -Name RegOutlookOptions$Ver -Scope Script -Value "$($KeyPrefix)Software\Microsoft\Office\$Ver.0\Outlook\Options"
		New-Variable -Name RegPolicyOptions$Ver  -Scope Script -Value "$($KeyPrefix)Software\Policies\Microsoft\Office\$Ver.0\Outlook\Options"
	}

	$script:RegValShutdown  = "FastShutdownBehavior"
	$script:RegValEnableAll = "EnableLogging"
	$script:RegValEnableEtw = "EnableEtwLogging"
	$script:RegValEnableCon = "EnableConflictLogging"
}


<#
	Enable "Global Logging" for Outlook, all versions.
#>
function EnableGlobalLogging
{
Param (
	[bool]$fEnable
)
	if ($fEnable) { Write-Status "Enabling Global Logging for future Outlook." }
	else { Write-Status "Disabling Global Logging for future Outlook." }

	# For Office 2010
	SetTempRegValue $fEnable "$RegOutlookOptions14\Mail" $RegValEnableAll 1
	SetTempRegValue $fEnable "$RegPolicyOptions14\Shutdown" $RegValShutdown 2

	# For Office 2013
	SetTempRegValue $fEnable "$RegOutlookOptions15\Mail" $RegValEnableAll 1
	SetTempRegValue $fEnable "$RegPolicyOptions15\Shutdown" $RegValShutdown 2

	# For Office 2016+ / 365
	SetTempRegValue $fEnable "$RegOutlookOptions16\Mail" $RegValEnableAll 1
	SetTempRegValue $fEnable "$RegPolicyOptions16\Shutdown" $RegValShutdown 2
}


<#
	Enable a subset of "Global Logging" for Outlook, all versions since 2013.
	This is not invoked by default.
#>
function EnableAdvancedLogging
{
Param (
	[bool]$fEnable
)
	if ($fEnable) { Write-Status "Enabling Advanced Logging for future Outlook." }
	else { "Disabling Advanced Logging for future Outlook." }

	# For Office 2013
	SetTempRegValue $True "$RegOutlookOptions15\Mail" $RegValEnableEtw [int]$fEnable
	SetTempRegValue $fEnable "$RegPolicyOptions15\Shutdown" $RegValShutdown 2

	# For Office 2016+ / 365
	SetTempRegValue $True "$RegOutlookOptions16\Mail" $RegValEnableEtw [int]$fEnable
	SetTempRegValue $fEnable "$RegPolicyOptions16\Shutdown" $RegValShutdown 2
}


<#
	Enable a subset of "Global Logging" for Outlook, all versions since 2013.
	This is not invoked by default.
#>
function EnableSyncLogging
{
Param (
	[bool]$fEnable
)
<#
	EnableConflictLogging:
	0 = Never save Modification Resolution logs
	1 = Always save Modification Resolution logs 
	2 = Save Modification Resolution logs when a "critical conflict" occurs 
#>
	if ($fEnable) { Write-Status "Enabling Sync Logging for future Outlook." }
	else { Write-Status "Disabling Sync Logging for future Outlook." }

	# For Office 2013
	SetTempRegValue $fEnable $RegOutlookOptions15 $RegValEnableCon 1

	# For Office 2016+ / 365
	SetTempRegValue $fEnable $RegOutlookOptions16 $RegValEnableCon 1
}


<#
	Invoke one of the EnableLogging commands.
	https://support.microsoft.com/en-us/topic/how-to-enable-global-and-advanced-logging-for-microsoft-outlook-15c74560-2aaa-befd-c256-7c8823b1aefa
#>
function EnableLogging
{
Param (
	[bool]$fEnable
)
	EnableLogging_Office $fEnable

	# Adjust the logging registry paths to the signed-in user.
	SetLoggedOnRegistry

	# Invoke only one:
	EnableGlobalLogging $fEnable
	# EnableAdvancedLogging $fEnable # a subset
	# EnableSyncLogging $fEnable # a subset
}


<#
	If -Verbose then write out any relevant, non-zero registry value.
#>
function WriteLoggingStatus
{
	if (!(DoVerbose)) { return }

	WriteLoggingStatus_Office

	SetLoggedOnRegistry

	# For Office 2010
	WriteLoggingValue "$RegOutlookOptions14\Mail" $RegValEnableAll
	WriteLoggingValue "$RegPolicyOptions14\Shutdown" $RegValShutdown

	# For Office 2013
	WriteLoggingValue "$RegOutlookOptions15\Mail" $RegValEnableAll
	WriteLoggingValue "$RegOutlookOptions15\Mail" $RegValEnableEtw
	WriteLoggingValue  $RegOutlookOptions15 $RegValEnableCon
	WriteLoggingValue "$RegPolicyOptions15\Shutdown" $RegValShutdown

	# For Office 2016+ / 365
	WriteLoggingValue "$RegOutlookOptions16\Mail" $RegValEnableAll
	WriteLoggingValue "$RegOutlookOptions16\Mail" $RegValEnableEtw
	WriteLoggingValue  $RegOutlookOptions16 $RegValEnableCon
	WriteLoggingValue "$RegPolicyOptions16\Shutdown" $RegValShutdown
}


<#
	Copy all log files newer than a certain date-time to: $Env:LocalAppDataAlt\MSO-Scripts\Outlook\<Date-Time>\...
	See: https://support.microsoft.com/en-us/topic/how-to-enable-global-and-advanced-logging-for-microsoft-outlook-15c74560-2aaa-befd-c256-7c8823b1aefa
#>
function GatherLogs
{
Param (
	[switch]$Shh
)
	EnsureTracePath # Sets TracePath, TempAlt, etc.

	$LogPath = "$script:TracePath\Outlook"

	$StartTime = GetProfileStartDateTime $TraceParams.InstanceName

	# Create a list of files to gather.

	$PathList = (
		@{ Path="$Env:Temp\Diagnostics\Outlook\*"; Include="*.log" },
		@{ Path="$Env:Temp\Diagnostics\Outlook\Additional\*"; Include="*.log" },
		@{ Path="$Env:Temp\*"; Include="*.dat","*.etl","*.htm","*.log","*.xml" },
		@{ Path="$Env:Temp\OlkAS\*" },
		@{ Path="$Env:Temp\EASLogFiles\*" },
		@{ Path="$Env:Temp\OlkCalLogs\*" },
		@{ Path="$Env:Temp\Outlook Logging\*" } )

	if ($Env:TempAlt -ne $Env:Temp)
	{
		$PathList += (
			@{ Path="$Env:TempAlt\Diagnostics\Outlook\*"; Include="*.log" },
			@{ Path="$Env:TempAlt\Diagnostics\Outlook\Additional\*"; Include="*.log" },
			@{ Path="$Env:TempAlt\*"; Include="*.dat","*.etl","*.htm","*.log","*.xml" },
			@{ Path="$Env:TempAlt\OlkAS\*" },
			@{ Path="$Env:TempAlt\EASLogFiles\*" },
			@{ Path="$Env:TempAlt\OlkCalLogs\*" },
			@{ Path="$Env:TempAlt\Outlook Logging\*" } )
	}

	[string[]] $Logs = $Null
	foreach ($PathSpec in $PathList)
	{
		# Not in PSv2: -Recurse -File for Get-ChildItem
		$LogsT = Get-Item @PathSpec -ErrorAction:SilentlyContinue | Where-Object {$_.LastWriteTime -ge $StartTime}
		Write-Status ($LogsT | Measure-Object).Count "log files on path:" $PathSpec.Path
		if ($LogsT) { $Logs += $LogsT.FullName }
	}

	$Result = DoGatherLogs $StartTime $LogPath $Logs

	switch ($Result)
	{
	Collected
	{
		if (!$Shh) { Write-Msg "`nOutlook logs are in this folder: $(GetEnvPath $script:TracePath)`n$script:TracePath`n" }
		break
	}
	Started
	{
		Write-Warn "No Outlook logs were collected. (Outlook may not have run.)"
		break
	}
	Error
	{
		# Failed to copy!
		Write-Msg "`nOutlook logs are here:`n$($Logs | Out-String)"
		break
	}
	View { break } # "Collection of logs was not started..."
	}
}


<#
	If Outlook is still running, ask them to quit Outlook or cancel tracing.
	When stopping, ignore processes started before StartTime.
#>
function CheckProcessState
{
Param (
	[Nullable[DateTime]]$StartTime,
	[switch]$Warn
)
	$ProcessNames = CheckProcessListState "Outlook" $Null -Warn:$Warn

	# $Null or "Outlook.exe"
	if ($ProcessNames)
	{
		if ($Warn)
		{
			if ($StartTime)
			{
				# Tracing is supposed to stop.	
				Write-Action "Please either quit $ProcessNames and run: $(GetScriptCommand) Stop -Verbose"
				Write-Action "Or run: $(GetScriptCommand) Cancel"
			}
			else
			{
				# Tracing is supposed to start.
				Write-Action "Please quit $ProcessNames and run: $(GetScriptCommand) Start"
			}
		}
		return $False
	}
	return $True
}


<#
	Control Outlook logging.
	Return one of: Success (no-op), Started, Collected, Error.
#>
function ProcessOutlookTraceCommand
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
			Write-Warn "`tOutlook logging is already started."
			Write-Warn "`tPlease exercise Outlook, then run: $(GetScriptCommand) Stop"
			Write-Warn "`tOr run: $(GetScriptCommand) Cancel"
			return [ResultValue]::Success # Already stopped, no problem.
		}

		if (!(CheckProcessState $Null -Warn)) { return [ResultValue]::Error }

		# Log a start time here before any log files are created. We'll use it later.
		# This will also get noticed by any status call to: ListRunningProfiles
		SetProfileStartTime $TraceParams.InstanceName

		EnableLogging $True

		return [ResultValue]::Started
		}
	"Stop"
		{
		EnableLogging $False

		$StartTime = GetProfileStartDateTime $TraceParams.InstanceName

		if (!$StartTime)
		{
			Write-Warn "`tOutlook logging is already stopped."
			return [ResultValue]::Success # Already stopped, no problem.
		}

		# Wait for Outlook to quit, or not?
		# if (!(CheckProcessState $StartTime -Warn)) { return [ResultValue]::Error }
		if (!(CheckProcessState $StartTime)) { Write-Warn "`tCollecting logs anyway." }

		GatherLogs -Shh:$Shh

		ClearProfileStartTime $TraceParams.InstanceName

		return [ResultValue]::Collected
		}
	"Cancel"
		{
		EnableLogging $False

		ClearProfileStartTime $TraceParams.InstanceName

		return [ResultValue]::Success
		}
	"Status"
		{
		$StartTime = GetProfileStartDateTime $TraceParams.InstanceName
		if ($StartTime)
		{
			Write-Msg "`tOutlook logging began at $StartTime"
		}
		else
		{
			Write-Msg "`tOutlook logging has not started."
		}
		WriteLoggingStatus

		WriteRecentLogPath "Outlook"

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

	# Handle log files produced by Outlook.

	$LogResult = ProcessOutlookTraceCommand $Command -Shh:$Shh
	ValidateResult "ProcessOutlookTraceCommand" $LogResult

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
			if ($ETWTrace) { Write-Msg "Outlook logging is enabled, and ETW tracing has started." }
			Write-Msg "Exercise Outlook now. Then quit Outlook and run: $(GetScriptCommand) Stop"
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
		if (!$ViewerParams.TraceFilePath) { $ViewerParams.TraceFilePath = GetRecentTraceFilePath "Outlook" $ViewerParams.TraceName }
		LaunchViewer @ViewerParams
		break
	}
	Error
	{
		# ProcessTraceCommand failed.
		$Result = ProcessOutlookTraceCommand "Cancel"
		exit 1
	}
	}

exit 0 # Success
