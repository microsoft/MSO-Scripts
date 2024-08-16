<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	CPU Samples, Thread Dispatch, Processes, Modules

	.DESCRIPTION

	.\TraceCPU Start [-Lean] [-Loop] [-CLR] [-JS]
	.\TraceCPU Stop [-WPA]
	.\TraceCPU View [-Path <path>\MSO-Trace-CPU.etl]
	.\TraceCPU Status
	.\TraceCPU Cancel
	    -Lean: Reduced data collection: no call stacks.
	    -Loop: Record only the last few minutes of activity (circular memory buffer). 
	    -CLR:  Resolve call stacks for C# (Common Language Runtime).
	    -JS:   Resolve call stacks for JavaScript.
	    -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	    -Path: Optional path to a previously collected trace.
	    -Verbose

	These scripts work with pre-Win10 versions of the Windows Performance Toolkit (WPT).

	.LINK

	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows
	https://learn.microsoft.com/en-us/shows/defrag-tools/39-windows-performance-toolkit
#>

[CmdletBinding(DefaultParameterSetName = "Start")]
Param(
	# "Start, Stop, Status, Cancel, View"
	[Parameter(Position=0)]
	[string]$Command,

	# "Reduced data collection: no call stacks"
	[Parameter(ParameterSetName="Start")]
	[switch]$Lean,

	# Compatibility only
	[Parameter(ParameterSetName="Start")]
	[switch]$Lite,

	# Record only the last few minutes of activity (circular memory buffer).
	[Parameter(ParameterSetName="Start")]
	[switch]$Loop,

	# "Support Common Language Runtime / C#"
	[Parameter(ParameterSetName="Start")]
	[switch]$CLR,

	# "Support JavaScript"
	[Parameter(ParameterSetName="Start")]
	[switch]$JS,

	# "Launch WPA after collecting the trace"
	[Parameter(ParameterSetName="Stop")]
	[switch]$WPA,

	# "Optional path to a previously collected trace: MSO-Trace-CPU.etl"
	[Parameter(ParameterSetName="View")]
	[string]$Path

	# [switch]$Verbose # implicit
)

if (!$Path) { $Path = $Null } # for PSv2

# ===== CUSTOMIZE THIS =====

	$TraceParams =
	@{
		RecordingProfiles =
		@(
			# Capture CPU Samples and Dispatcher Info with call stacks for all processes.
			".\WPRP\CPU.wprp.Verbose"
			".\WPRP\OfficeProviders.wprp.Light" # Code Markers
		<#
			^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ReadMe.txt

			"Registry" # Built-in
			".\WPRP\FileDiskIO.wprp.Verbose"
			"c:\MyProfiles\MyRecordingProfile.wprp"
		#>
		)

		# This is the arbitrary name of the tracing session/instance:
		InstanceName = "MSO-Trace-CPU"
	}

	# Change the base recording profile:
	# Capture only "Lean" CPU Samples for all processes/modules.
	if ($Lean -or $Lite) { $TraceParams.RecordingProfiles = @( ".\WPRP\CPU.wprp.Light" ) } # Just this one recording profile.

	$ViewerParams =
	@{
		# This configuration file organizes the data in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\CPU.wpaProfile"

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

# Main

	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS
	ValidateResult "ProcessTraceCommand" $Result

	switch ($Result)
	{
	Started   { Write-Msg "ETW CPU tracing has begun.`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n" }
	Collected { WriteTraceCollected $TraceParams.InstanceName; if ($WPA) { LaunchViewer @ViewerParams } }
	View      { LaunchViewer @ViewerParams }
	Error     { exit 1 }
	}

exit 0 # Success
