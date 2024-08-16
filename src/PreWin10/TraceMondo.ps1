<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	CPU Samples, Thread Dispatch, File I/O, Office Logging Providers, ThreadPool, Processes, Modules

	.DESCRIPTION

	.\TraceMondo Start [-Loop] [-CLR] [-JS]
	.\TraceMondo Stop [-WPA]
	.\TraceMondo View [-Path <path>\MSO-Trace-CPU.etl]
	.\TraceMondo Status
	.\TraceMondo Cancel
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

	# "Optional path to a previously collected trace: MSO-Trace-Mondo.etl"
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
			".\WPRP\CPU.wprp.Verbose"
			".\WPRP\FileDiskIO.wprp.Light"
			".\WPRP\OfficeProviders.wprp"
			".\WPRP\Network.wprp.Verbose"
			".\WPRP\ThreadPool.wprp"
			".\WPRP\Defender.wprp.Light"

		<#	^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ReadMe.txt

			"Registry" # Built-in
			".\WPRP\FileDiskIO.wprp.Verbose"
			"c:\MyProfiles\MyRecordingProfile.wprp"
		#>
		)

		# This is the arbitrary name of the tracing session/instance:
		InstanceName = "MSO-Trace-Mondo"
	}

	$ViewerParams =
	@{
		# This configuration file organizes the data in the WPA viewer.
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

# Main
	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS
	ValidateResult "ProcessTraceCommand" $Result

	switch ($Result)
	{
	Started
		{
		# Call stacks with symbols are available for File I/O in Windows 8.0 (v6.2) and above.  Disk I/O traces do collect call stacks.
		if (!(CheckOSVersion '6.2.0')) { Write-Warn "Warning: Call stacks with symbols for File I/O traces are available starting with Windows 8.0`n" }

		Write-Msg "ETW tracing has begun.`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n"
		}
	Collected { WriteTraceCollected $TraceParams.InstanceName; if ($WPA) { LaunchViewer @ViewerParams } }
	View      { LaunchViewer @ViewerParams }
	Error     { exit 1 }
	}

exit 0 # Success
