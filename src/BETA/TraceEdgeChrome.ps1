<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	CPU Samples, Thread Dispatch, File I/O, Office Logging Providers, ThreadPool, Processes, Modules

	.DESCRIPTION

	.\TraceEdgeChrome Start [-Loop] [-CLR]
	.\TraceEdgeChrome Stop [-WPA [-FastSym]]
	.\TraceEdgeChrome View [-Path <path>\MSO-Trace-EdgeChrome.etl|.wpapk] [-FastSym]
	.\TraceEdgeChrome Status
	.\TraceEdgeChrome Cancel
	  -Loop: Record only the last few minutes of activity (circular memory buffer). 
	  -CLR:  Resolve call stacks for C# (Common Language Runtime).
	  -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	  -Path: Optional path to a previously collected trace.
	  -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	            See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	  -Verbose

	.LINK

	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows
	https://learn.microsoft.com/en-us/shows/defrag-tools/39-windows-performance-toolkit
#>

[CmdletBinding(DefaultParameterSetName = "View")]
Param(
	# "Start, Stop, Status, Cancel, View"
	[Parameter(Position=0)]
	[string]$Command,

	# Record only the last few minutes of activity (circular memory buffer).
	[Parameter(ParameterSetName="Start")]
	[switch]$Loop,

	# "Support Common Language Runtime / C#"
	[Parameter(ParameterSetName="Start")]
	[switch]$CLR,

	# "Launch WPA after collecting the trace"
	[Parameter(ParameterSetName="Stop")]
	[switch]$WPA,

	# "Optional path to a previously collected trace: MSO-Trace-EdgeChrome.etl"
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null,

	# "Faster symbol resolution by loading only from SymCache, not PDB"
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$FastSym

	# [switch]$Verbose # implicit
)

# ===== CUSTOMIZE THIS =====

	$TraceParams =
	@{
		RecordingProfiles =
		@(
			".\WPRP\MSEdge.wprp!MSEdge_Filtered" # Includes Chrome
			"..\WPRP\JS.wprp!JS"
		#	"..\WPRP\CPU.wprp!CPU-Dispatch"
		#	"..\WPRP\FileDiskIO.wprp!FileAndDiskIO"
		#	"..\WPRP\OfficeProviders.wprp!OfficeLogging"
		#	"..\WPRP\Handles.wprp!AllHandles"
		#	"..\WPRP\Defender.wprp!DefenderFull"

		<#	^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ..\ReadMe.txt

			"Registry" # Built-in
			"..\WPRP\FileDiskIO.wprp!FileIO"
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
			# Optional: Register ETW Provider Manifests not registered by default.
			# See: .\OETW\ReadMe.txt
			".\OETW\EdgeETW.man"
			".\OETW\ChromeETW.man"
		)

		# This is the arbitrary name of the tracing session/instance:
		InstanceName = "MSO-Trace-EdgeChrome"
	}

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\MSEdge.wpaProfile"

		# The default trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

# ===== END CUSTOMIZE ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = "$PSScriptRoot\.."
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"

# Main

	if ($Command -ne "View")
	{
		$Win10VerMin = '10.0.15002' # Min version for both Windows and WPR for <EventNameFilters>
		$Win10VerCur = [Environment]::OSVersion.Version

		if ($Win10VerCur -lt $Win10VerMin)
        	{
			Write-Err "This trace requires Windows 10 v$Win10VerMin. Current version is: v$Win10VerCur"
			exit 1
		}

		CheckPrerequisites # Sets script:WPR_Win10Ver

		if (!(!$script:WPR_PreWin10 -and ($script:WPR_Win10Ver -ge $Win10VerMin)))
        	{
			Write-Err "This trace requires WPR.exe v$Win10VerMin. Current version is: v$script:WPR_Win10Ver"
			Write-Err $script:WPR_Path
			exit 1
		}
	}

	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR

	switch ($Result)
	{
	Started   { Write-Msg "ETW tracing has begun.`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n" }
	Collected { WriteTraceCollected $TraceParams.InstanceName } # $WPA switch
	View      { $WPA = $True }
	Success   { $WPA = $False }
	Error     { exit 1 }
	}

	if ($WPA)
	{
		LaunchViewer @ViewerParams -FastSym:$FastSym

		Write-Warn "`nPlease be patient, as it may take several minutes for WPA to organize the thousands of events."
		Write-Status "This is mainly due to the Regions of Interest (Timelines View)."
	}

exit 0 # Success
