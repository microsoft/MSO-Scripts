<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	RAM Usage (Reference Set), VirtualAlloc, Processes, Modules

	.DESCRIPTION

	.\TraceMemory Start [-Lean] [-Loop] [-CLR] [-JS]
	.\TraceMemory Stop [-WPA]
	.\TraceMemory View [-Path <path>\MSO-Trace-Memory.etl]
	.\TraceMemory Status
	.\TraceMemory Cancel
	    -Lean: Reduced data collection: memory snapshots every 0.5 sec.
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

	# "Reduced data collection: memory snapshots every 0.5 sec."
	[Parameter(ParameterSetName="Start")]
	[switch]$Lean,

	# "Same as 'Lean' (for compatiblity)"
	[Parameter(ParameterSetName="Start")]
	[switch]$Sample,

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

	# "Optional path to a previously collected trace: MSO-Trace-Memory.etl"
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
			# Capture Memory Info (Reference Set, etc.) with call stacks for all processes.
			# To see the available profiles, run: wpr -profiles .\WPRP\Memory.wprp
			".\WPRP\Memory.wprp.Verbose"
			".\WPRP\OfficeProviders.wprp.Light" # Code Markers
		<#
			^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ReadMe.txt

			"Registry" # Built-in
			".\WPRP\FileDiskIO.wprp"
			"c:\MyProfiles\MyRecordingProfile.wprp.Light"
		#>
		)

		# This is the arbitrary name of the tracing session/instance.
		InstanceName = "MSO-Trace-Memory"
	}

	# Capture only snapshots per process every 1/2 second:
	if ($Lean -or $Sample) { $TraceParams.RecordingProfiles = @( ".\WPRP\Memory.wprp.Light" ) } # Just this one recording profile.

	$ViewerParams =
	@{
		# This configuration file organizes the data in the WPA viewer.
		ViewerConfig = ".\WPAP\Memory.wpaProfile"

		# The trace file name is: <InstanceName>.etl
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

	# Memory tracing of this type is very limited before Win8.0 (v6.2).
	if ($Lean -or $Sample)
	{
		if (!(CheckOSVersion '6.2.0'))
		{
			Write-Err "`nThis type of memory sampling is available only in Windows 8.0 and later."
			exit 1
		}
	}

	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS
	ValidateResult "ProcessTraceCommand" $Result

	switch ($Result)
	{
	Started
	{
		if (!(CheckOSVersion '6.2.0')) { Write-Warn "Warning: Call stacks for this type of memory tracing are available in Windows 8.0 and later.`n" }

		Write-Msg "ETW Memory tracing has begun."
		Write-Msg "Exercise the application, then run: $(GetScriptCommand) Stop [-WPA]"
		Write-Msg "(Do not quit the application until tracing is stopped!)"
		Write-Msg
	}
	Collected { WriteTraceCollected $TraceParams.InstanceName; if ($WPA) { LaunchViewer @ViewerParams } }
	View      { LaunchViewer @ViewerParams }
	Error     { exit 1 }
	}

exit 0 # Success
