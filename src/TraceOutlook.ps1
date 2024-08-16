<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture / View an ETW trace and other logs from Outlook.
	See: https://support.microsoft.com/en-us/topic/how-to-enable-global-and-advanced-logging-for-microsoft-outlook-15c74560-2aaa-befd-c256-7c8823b1aefa
	See: https://support.microsoft.com/en-us/office/what-is-the-enable-logging-troubleshooting-option-0fdc446d-d1d4-42c7-bd73-74ffd4034af5
	See: https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/collect-office-diagnostic-logs

	.DESCRIPTION

	.\TraceOutlook Start [-All] [-Loop] [-CLR] [-JS] [-Shh]
	.\TraceOutlook Stop [-WPA [-FastSym]] [-Shh]
	.\TraceOutlook View [-Path <path>\MSO-Trace-Outlook.etl | <path>\<Name>.zip] [-FastSym]
	.\TraceOutlook Status
	.\TraceOutlook Cancel
	  -All:  Capture extra ETW providers (network activity) and extra logging sources.
	  -Loop: Record only the last few minutes of activity (circular memory buffer). 
	  -CLR:  Resolve call stacks for C# (Common Language Runtime).
	  -JS:   Resolve call stacks for JavaScript.
	  -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	  -Path: Optional path to a previously collected trace.
	  -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	            See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	  -Shh:  Suppress explanatory output.
	  -Verbose

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

	# Optional path to a previously collected trace: MSO-Trace-Outlook.etl OR <path>\<Name>.zip
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null

	# [switch]$Verbose # implicit
)

# ===== CUSTOMIZE THIS =====

	# Apps to track: Outlook
	# These are for UI and also correspond to certain registry keys. See: SetLoggedOnRegistry_Outlook
	$script:OfficeAppList = ,"Outlook"

	# Processes to track: Outlook.exe
	$script:OfficeProcessList = ,"Outlook"

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
		InstanceName = "MSO-Trace-Outlook"

	} # TraceParams

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
		)


		NewFolders =
		@(
			# These folders must be created in advance for the log files.

			# General Office, as defined above
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
	Create the logging registry key paths and values.
	If needed, switch them to the path of the logged-on user for Outlook.
	See: https://support.microsoft.com/en-us/topic/how-to-enable-global-and-advanced-logging-for-microsoft-outlook-15c74560-2aaa-befd-c256-7c8823b1aefa
#>
function SetLoggedOnRegistry_Outlook
{
	if (GetVariable "RegOutlookOptions16") { return } # for strict mode

	$KeyPrefix = _GetUserRegKeyPrefix

	# $script:RegOutlookOptions## = "HKCU:\Software\Microsoft\Office\##.0\Outlook\Options"
	# $script:RegPolicyOptions##  = "HKCU:\Software\Policies\Microsoft\Office\##.0\Outlook\Options"

	for ($Ver = 14; $Ver -le 16; $Ver += 1)
	{
		New-Variable -Name RegOutlookOptions$Ver -Scope Script -Value "HKCU:\Software\Microsoft\Office\$Ver.0\Outlook\Options"
		New-Variable -Name RegPolicyOptions$Ver  -Scope Script -Value "HKCU:\Software\Policies\Microsoft\Office\$Ver.0\Outlook\Options"

		if ($KeyPrefix)
		{
			New-Variable -Name RegOutlookOptions$($Ver)Alt -Scope Script -Value "$($KeyPrefix)Software\Microsoft\Office\$Ver.0\Outlook\Options"
			New-Variable -Name RegPolicyOptions$($Ver)Alt  -Scope Script -Value "$($KeyPrefix)Software\Policies\Microsoft\Office\$Ver.0\Outlook\Options"	
		}
	}

	$script:RegValShutdown  = "FastShutdownBehavior"
	$script:RegValEnableAll = "EnableLogging"
	$script:RegValEnableEtw = "EnableEtwLogging"
	$script:RegValEnableCon = "EnableConflictLogging"
	$script:RegValEnableCal = "EnableCalendarLogging"
}


<#
	Enable "Global Logging" for Outlook, all versions.
	These registry values must also be represented in: WriteLoggingStatus_Outlook
#>
function EnableGlobalLogging
{
Param (
	[bool]$fEnable
)
	if ($fEnable) { Write-Status "Enabling Global Logging for future Outlook." }
	else { Write-Status "Resetting Global Logging for future Outlook." }

	# For Office 2010
	SetTempRegValue $fEnable "$script:RegOutlookOptions14\Mail"     $script:RegValEnableAll 1
	SetTempRegValue $fEnable "$script:RegOutlookOptions14\Calendar" $script:RegValEnableCal 1
	SetTempRegValue $fEnable "$script:RegPolicyOptions14\Shutdown"  $script:RegValShutdown  2

	# For Office 2013
	SetTempRegValue $fEnable "$script:RegOutlookOptions15\Mail"     $script:RegValEnableAll 1
	SetTempRegValue $fEnable "$script:RegOutlookOptions15\Calendar" $script:RegValEnableCal 1
	SetTempRegValue $fEnable "$script:RegPolicyOptions15\Shutdown"  $script:RegValShutdown  2

	# For Office 2016+ / 365
	SetTempRegValue $fEnable "$script:RegOutlookOptions16\Mail"     $script:RegValEnableAll 1
	SetTempRegValue $fEnable "$script:RegOutlookOptions16\Calendar" $script:RegValEnableCal 1
	SetTempRegValue $fEnable "$script:RegPolicyOptions16\Shutdown"  $script:RegValShutdown  2

	if (GetVariable "RegOutlookOptions16Alt")
	{
		# For Office 2010
		SetTempRegValue $fEnable "$script:RegOutlookOptions14Alt\Mail"     $script:RegValEnableAll 1
		SetTempRegValue $fEnable "$script:RegOutlookOptions14Alt\Calendar" $script:RegValEnableCal 1
		SetTempRegValue $fEnable "$script:RegPolicyOptions14Alt\Shutdown"  $script:RegValShutdown  2

		# For Office 2013
		SetTempRegValue $fEnable "$script:RegOutlookOptions15Alt\Mail"     $script:RegValEnableAll 1
		SetTempRegValue $fEnable "$script:RegOutlookOptions15Alt\Calendar" $script:RegValEnableCal 1
		SetTempRegValue $fEnable "$script:RegPolicyOptions15Alt\Shutdown"  $script:RegValShutdown  2

		# For Office 2016+ / 365
		SetTempRegValue $fEnable "$script:RegOutlookOptions16Alt\Mail"     $script:RegValEnableAll 1
		SetTempRegValue $fEnable "$script:RegOutlookOptions16Alt\Calendar" $script:RegValEnableCal 1
		SetTempRegValue $fEnable "$script:RegPolicyOptions16Alt\Shutdown"  $script:RegValShutdown  2
	}
}


<#
	Enable a subset of "Global Logging" for Outlook, all versions since 2013.
	These registry values must also be represented in: WriteLoggingStatus_Outlook
#>
function EnableAdvancedLogging
{
Param (
	[bool]$fEnable
)
	if ($fEnable) { Write-Status "Enabling Advanced Logging for future Outlook." }
	else { Write-Status "Resetting Advanced Logging for future Outlook." }

	# For Office 2013
	SetTempRegValue $fEnable "$script:RegOutlookOptions15\Mail"    $script:RegValEnableEtw 1
	SetTempRegValue $fEnable "$script:RegPolicyOptions15\Shutdown" $script:RegValShutdown  2

	# For Office 2016+ / 365
	SetTempRegValue $fEnable "$script:RegOutlookOptions16\Mail"    $script:RegValEnableEtw 1
	SetTempRegValue $fEnable "$script:RegPolicyOptions16\Shutdown" $script:RegValShutdown  2

	if (GetVariable "RegOutlookOptions16Alt")
	{
		# For Office 2013
		SetTempRegValue $fEnable "$script:RegOutlookOptions15Alt\Mail"    $script:RegValEnableEtw 1
		SetTempRegValue $fEnable "$script:RegPolicyOptions15Alt\Shutdown" $script:RegValShutdown  2

		# For Office 2016+ / 365
		SetTempRegValue $fEnable "$script:RegOutlookOptions16Alt\Mail"    $script:RegValEnableEtw 1
		SetTempRegValue $fEnable "$script:RegPolicyOptions16Alt\Shutdown" $script:RegValShutdown  2
	}
}


<#
	Enable a subset of "Global Logging" for Outlook, all versions since 2013.
	These registry values must also be represented in: WriteLoggingStatus_Outlook
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
	else { Write-Status "Resetting Sync Logging for future Outlook." }

	# For Office 2013
	SetTempRegValue $fEnable $script:RegOutlookOptions15 $script:RegValEnableCon 1

	# For Office 2016+ / 365
	SetTempRegValue $fEnable $script:RegOutlookOptions16 $script:RegValEnableCon 1

	if (GetVariable "RegOutlookOptions16Alt")
	{
		# For Office 2013
		SetTempRegValue $fEnable $script:RegOutlookOptions15Alt $script:RegValEnableCon 1

		# For Office 2016+ / 365
		SetTempRegValue $fEnable $script:RegOutlookOptions16Alt $script:RegValEnableCon 1
	}
}


<#
	Invoke the Enable*Logging commands.
	https://support.microsoft.com/en-us/topic/how-to-enable-global-and-advanced-logging-for-microsoft-outlook-15c74560-2aaa-befd-c256-7c8823b1aefa
#>
function EnableLogging_Outlook
{
Param (
	[bool]$fEnable,
	[switch]$All
)
	# Adjust the logging registry paths to the signed-in user.
	SetLoggedOnRegistry_Outlook

	EnableGlobalLogging $fEnable

	if ($All -or !$fEnable)
	{
		EnableAdvancedLogging $fEnable # a subset
		EnableSyncLogging $fEnable # a subset
	}
}


<#
	If -Verbose then write out any relevant, non-zero registry value.
#>
function WriteLoggingStatus_Outlook
{
	if (!(DoVerbose)) { return }

	SetLoggedOnRegistry_Outlook

	# For Office 2010
	WriteLoggingValue "$script:RegOutlookOptions14\Mail"     $script:RegValEnableAll
	WriteLoggingValue "$script:RegOutlookOptions14\Calendar" $script:RegValEnableCal
	WriteLoggingValue "$script:RegPolicyOptions14\Shutdown"  $script:RegValShutdown

	# For Office 2013
	WriteLoggingValue "$script:RegOutlookOptions15\Mail"     $script:RegValEnableAll
	WriteLoggingValue "$script:RegOutlookOptions15\Mail"     $script:RegValEnableEtw
	WriteLoggingValue "$script:RegOutlookOptions15\Calendar" $script:RegValEnableCal
	WriteLoggingValue  $script:RegOutlookOptions15           $script:RegValEnableCon
	WriteLoggingValue "$script:RegPolicyOptions15\Shutdown"  $script:RegValShutdown

	# For Office 2016+ / 365
	WriteLoggingValue "$script:RegOutlookOptions16\Mail"     $script:RegValEnableAll
	WriteLoggingValue "$script:RegOutlookOptions16\Mail"     $script:RegValEnableEtw
	WriteLoggingValue "$script:RegOutlookOptions16\Calendar" $script:RegValEnableCal
	WriteLoggingValue  $script:RegOutlookOptions16           $script:RegValEnableCon
	WriteLoggingValue "$script:RegPolicyOptions16\Shutdown"  $script:RegValShutdown

	if (GetVariable "RegOutlookOptions16Alt")
	{
		# For Office 2010
		WriteLoggingValue "$script:RegOutlookOptions14Alt\Mail"     $script:RegValEnableAll
		WriteLoggingValue "$script:RegOutlookOptions14Alt\Calendar" $script:RegValEnableCal
		WriteLoggingValue "$script:RegPolicyOptions14Alt\Shutdown"  $script:RegValShutdown

		# For Office 2013
		WriteLoggingValue "$script:RegOutlookOptions15Alt\Mail"     $script:RegValEnableAll
		WriteLoggingValue "$script:RegOutlookOptions15Alt\Mail"     $script:RegValEnableEtw
		WriteLoggingValue "$script:RegOutlookOptions15Alt\Calendar" $script:RegValEnableCal
		WriteLoggingValue  $script:RegOutlookOptions15Alt           $script:RegValEnableCon
		WriteLoggingValue "$script:RegPolicyOptions15Alt\Shutdown"  $script:RegValShutdown

		# For Office 2016+ / 365
		WriteLoggingValue "$script:RegOutlookOptions16Alt\Mail"     $script:RegValEnableAll
		WriteLoggingValue "$script:RegOutlookOptions16Alt\Mail"     $script:RegValEnableEtw
		WriteLoggingValue "$script:RegOutlookOptions16Alt\Calendar" $script:RegValEnableCal
		WriteLoggingValue  $script:RegOutlookOptions16Alt           $script:RegValEnableCon
		WriteLoggingValue "$script:RegPolicyOptions16Alt\Shutdown"  $script:RegValShutdown
	}
}


<#
	Copy all files newer than a certain date-time from $Env:Temp and $Env:TempAlt :
		$Env:Temp\*.dat
		$Env:Temp\*.etl
		$Env:Temp\*.htm
		$Env:Temp\*.log
		$Env:Temp\*.xml
		$Env:Temp\OlkAS\*
		$Env:Temp\OlkCalLogs\*
		$Env:Temp\EASLogFiles\*
		$Env:Temp\Outlook Logging\*
	To:
		$script:TracePath\Outlook\<Date-Time>\...

	See: https://support.microsoft.com/en-us/topic/how-to-enable-global-and-advanced-logging-for-microsoft-outlook-15c74560-2aaa-befd-c256-7c8823b1aefa
	See: https://support.microsoft.com/en-us/office/what-is-the-enable-logging-troubleshooting-option-0fdc446d-d1d4-42c7-bd73-74ffd4034af5
#>
function GatherLogs
{
Param (
	[string[]]$ExtraPaths,
	[switch]$Shh
)
	EnsureTracePath # Sets TracePath, TempAlt, etc.

	$LogPath = "$script:TracePath\Outlook"

	$StartTime = GetProfileStartDateTime $TraceParams.InstanceName

	[System.Collections.Hashtable[]]$PathList = $Null;

	# List paths of Office logs to gather.

	foreach ($Process in $script:OfficeProcessList)
	{
		$PathList += ( @{ Path="$Env:Temp\Diagnostics\$Process\*" } )
	}

	# List paths of Extra logs to gather.

	foreach ($SearchPath in $ExtraPaths)
	{
		$PathList += ( @{ Path=$ExecutionContext.InvokeCommand.ExpandString($SearchPath) } )
	}

	# List paths of Outlook-specific logs to gather.

	$PathList += (
		@{ Path="$Env:Temp\*"; Include="*.dat","*.etl","*.htm","*.log","*.xml" },
		@{ Path="$Env:Temp\OlkAS\*" },
		@{ Path="$Env:Temp\OlkCalLogs\*" },
		@{ Path="$Env:Temp\EASLogFiles\*" },
		@{ Path="$Env:Temp\Outlook Logging\*" } )

	# If the apps _might_ be running in a different user context then also list those log paths.

	if ($Env:TempAlt -ne $Env:Temp)
	{
		$PathList += (
			@{ Path="$Env:TempAlt\*"; Include="*.dat","*.etl","*.htm","*.log","*.xml" },
			@{ Path="$Env:TempAlt\OlkAS\*" },
			@{ Path="$Env:TempAlt\EASLogFiles\*" },
			@{ Path="$Env:TempAlt\OlkCalLogs\*" },
			@{ Path="$Env:TempAlt\Outlook Logging\*" } )

		foreach ($SearchPath in $ExtraPaths)
		{
			$AltPath = ResolveAltPath $SearchPath
			if ($AltPath) { $PathList += ( @{ Path=$AltPath } ) }
		}

		foreach ($Process in $script:OfficeProcessList)
		{
			$PathList += ( @{ Path="$Env:TempAlt\Diagnostics\$Process\*" } )
		}
	}

	[string[]] $Logs = $Null
	foreach ($PathSpec in $PathList)
	{
		$LogsT = Get-ChildItem @PathSpec -Recurse -File -ErrorAction:SilentlyContinue | Where-Object {$_.LastWriteTime -ge $StartTime}
		Write-Status ($LogsT | Measure-Object).Count "log files found:" $PathSpec.Path
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

		$ProcessNames = CheckProcessListState $script:OfficeProcessList $Null

		if ($ProcessNames)
		{
			# Tracing is supposed to start.
			Write-Action "Please quit $ProcessNames and run: $(GetScriptCommand) Start"
			return [ResultValue]::Error
		}

		# Log a start time here before any log files are created. We'll use it later.
		# This will also get noticed by any status call to: ListRunningProfiles
		SetProfileStartTime $TraceParams.InstanceName

		EnableLogging_Office $True

		EnableLogging_Outlook $True -All:$All

		if ($All)
		{
			EnableExtraLogging $True $ExtraLogs.Registry $ExtraLogs.NewFolders
		}

		return [ResultValue]::Started
		}

	"Stop"
		{
		EnableLogging_Office $False

		EnableLogging_Outlook $False

		EnableExtraLogging $False $ExtraLogs.Registry $Null

		$StartTime = GetProfileStartDateTime $TraceParams.InstanceName

		if (!$StartTime)
		{
			Write-Warn "`tOutlook logging is already stopped."
			return [ResultValue]::Success # Already stopped, no problem.
		}

		$ProcessNames = CheckProcessListState $script:OfficeProcessList $StartTime

		# Require Outlook to quit, or not?
		# If the app is still running, we can't collect all of the log files (sharing error).

		# if ($ProcessNames) { Write-Warn "`tCollecting logs anyway." }

		if ($ProcessNames)
		{
			Write-Action "Please either quit $ProcessNames and run: $(GetScriptCommand) Stop -Verbose"
			Write-Action "Or run: $(GetScriptCommand) Cancel"
			return [ResultValue]::Error
		}

		GatherLogs $ExtraLogs.Files -Shh:$Shh

		ClearProfileStartTime $TraceParams.InstanceName

		return [ResultValue]::Collected
		}

	"Cancel"
		{
		EnableLogging_Office $False

		EnableLogging_Outlook $False

		EnableExtraLogging $False $ExtraLogs.Registry $Null

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

		WriteLoggingStatus_Office

		WriteLoggingStatus_Outlook

		WriteExtraLoggingStatus $ExtraLogs.Registry

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

	# Check PreWin10 now, Admin Privilege later.
	CheckPrerequisites -NoAdminCheck

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
		# $WPA switch
		break
	}
	View
	{
		if (!$ViewerParams.TraceFilePath) { $ViewerParams.TraceFilePath = GetRecentTraceFilePath "Outlook" $ViewerParams.TraceName }
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
		$Result = ProcessOutlookTraceCommand "Cancel"
		exit 1
	}
	}

	if ($WPA) { LaunchViewer @ViewerParams -FastSym:$FastSym }

exit 0 # Success
