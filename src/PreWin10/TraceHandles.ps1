<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	Kernel Handles, Modules

	.DESCRIPTION

	.\TraceHandles Start [-Loop] [-CLR] [-JS]
	.\TraceHandles Stop [-WPA]
	.\TraceHandles View [-Path <path>\MSO-Trace-Handles.etl]
	.\TraceHandles Status
	.\TraceHandles Cancel
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
	https://learn.microsoft.com/en-us/windows/desktop/SysInfo/object-categories
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

	# "Optional path to a previously collected trace: MSO-Trace-Handles.etl"
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
			# This XML file contains tracing parameters for Windows Kernel Object Handles.
			".\WPRP\Handles.wprp.Light"
			".\WPRP\OfficeProviders.wprp.Light" # Code Markers
		<#
			^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ReadMe.txt

			"Registry" # Built-in
			".\WPRP\FileDiskIO.wprp"
			"c:\MyProfiles\MyRecordingProfile.wprp"
		#>
		)

		# This is the arbitrary name of the tracing session/instance.
		InstanceName = "MSO-Trace-Handles"
	}

	$ViewerParams =
	@{
		# This configuration file organizes the data in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\Handles.wpaProfile"

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

	# Tracing kernel handles is available in Windows 8.0 (v6.2) and above.

	if (!(CheckOSVersion '6.2.0'))
	{
		Write-Err "`nHandle tracing is available starting with Windows 8.0"
		exit 1
	}

	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS
	ValidateResult "ProcessTraceCommand" $Result

	switch ($Result)
	{
	Started
		{
		Write-Msg "ETW Handle tracing has begun.`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n"

		if (CheckOSVersion '10.0.18315') { break }

		Write-Warn "Tracing only Kernel Object Handles: Process, Thread, Registry Key, File, etc."
		Write-Warn "To trace GDI and User Handles, a more recent version of Windows is required (10.0.18315+)."
		Write-Warn "See: https://learn.microsoft.com/en-us/windows/desktop/SysInfo/object-categories"
		Write-Warn
		}

	Collected { WriteTraceCollected $TraceParams.InstanceName; if ($WPA) { LaunchViewer @ViewerParams } }
	View      { LaunchViewer @ViewerParams }
	Error     { exit 1 }
	}

exit 0 # Success
