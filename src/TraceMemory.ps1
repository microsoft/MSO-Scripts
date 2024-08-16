<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	RAM Usage (Reference Set), VirtualAlloc, Processes, Modules

	.DESCRIPTION

	.\TraceMemory Start  [-Lean | -Lite | -Stats | -Snap] [-Loop] [-CLR] [-JS]
	.\TraceMemory Stop   [-Lean | -Lite | -Stats | -Snap] [-WPA [-FastSym]]
	.\TraceMemory View   [-Lean | -Lite | -Stats | -Snap] [-Path <path>\MSO-Trace-Memory.etl|.wpapk] [-FastSym]
	.\TraceMemory Status [-Lean | -Lite | -Stats | -Snap]
	.\TraceMemory Cancel [-Lean | -Lite | -Stats | -Snap]
	    -Lean:   Reduced data collection: Reference Set (RAM Impact), no stackwalks.
	    -Lite:   Reduced data collection: Private Commit Charge (Committed VMem Charged to Pagefile)
	    -Stats:  Reduced data collection: Memory stats every 0.5 sec.
	    -Snap:   Reduced data collection: Resident Set memory snapshot
	    -Loop:   Record only the last few minutes of activity (circular memory buffer). 
	    -CLR:    Resolve call stacks for C# (Common Language Runtime).
	    -JS:     Resolve call stacks for JavaScript.
	    -WPA:    Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	    -Path:   Optional path to a previously collected trace.
	    -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	              See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	    -Verbose

	.LINK

	https://github.com/microsoft/MSO-Scripts/wiki/Reference-Set
	https://github.com/microsoft/MSO-Scripts/wiki/Windows-Memory-Cheat-Sheet
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows
	https://learn.microsoft.com/en-us/shows/defrag-tools/39-windows-performance-toolkit
#>

[CmdletBinding(DefaultParameterSetName = "View")]
Param(
	# "Start, Stop, Status, Cancel, View"
	[Parameter(Position=0)]
	[string]$Command,

	# "Minimum data collection for Reference Set (RAM Impact)"
	[switch]$Lean,

	# "Minimum data collection for Private Commit Charge (Virtual Memory)"
	[switch]$Lite,

	# "Capture memory stats every 0.5 sec."
	[switch]$Stats,

	# "Capture Resident Set snapshot"
	[switch]$Snap,

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
	[string]$Path = $Null,

	# "Faster symbol resolution by loading only from SymCache, not PDB"
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$FastSym

	# [switch]$Verbose # implicit
)

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = $PSScriptRoot
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"

# ===== CUSTOMIZE THIS =====

	$TraceParams =
	@{
		RecordingProfiles =
		@(
			# Capture Memory Info (Reference Set, etc.) with call stacks for all processes.
			# To see the available profiles, run: wpr -profiles .\WPRP\Memory.wprp
			".\WPRP\Memory.wprp!ReferenceSet"
			".\WPRP\OfficeProviders.wprp!CodeMarkers" # Code Markers, HVAs, other light logging

		<#
			^^^ The first entry is the base recording profile for this script.
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
		)

		# This is the arbitrary name of the tracing session/instance.
		InstanceName = "MSO-Trace-Memory"
	}

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\BasicInfo.wpaProfile",
				".\WPAP\VirtualAlloc.wpaProfile",
				".\WPAP\MemFileAccess.wpaProfile",
				".\WPAP\MemStats.wpaProfile",
				".\WPAP\CommitCharge.wpaProfile",
				".\WPAP\RAM.wpaProfile"

		# The trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

	$Option = $Null
	if ($Lean)
	{
		$Option = 'Lean'
		# Stackwalks page in parts of 64-bit modules, somewhat like exception handling.
		# -Lean removes all stackwalking, including Office Code Markers.
		$TraceParams.RecordingProfiles = @(".\WPRP\Memory.wprp!LeanReferenceSet") # array[1]
		$ViewerParams.ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\MemFileAccess.wpaProfile", ".\WPAP\RAM.wpaProfile"
	}
	if ($Lite)
	{
		if ($Option) { Write-Err "Ignoring: -$Option" }
		$Option = 'Lite'
		# Tracing full Commit Charge is not 'Lite'. Private Commit Charge excludes Pagefile-backed Sections.
		$TraceParams.RecordingProfiles[0] = ".\WPRP\Memory.wprp!PrivateCommitCharge"
		$ViewerParams.ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\MemStats.wpaProfile", ".\WPAP\VirtualAlloc.wpaProfile", ".\WPAP\CommitCharge.wpaProfile"
	}
	if ($Stats)
	{
		if ($Option) { Write-Err "Ignoring: -$Option" }
		$Option = 'Stats'
		$TraceParams.RecordingProfiles[0] = ".\WPRP\Memory.wprp!MemoryStats"
		$ViewerParams.ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\MemStats.wpaProfile"
	}
	if ($Snap)
	{
		if ($Option) { Write-Err "Ignoring: -$Option" }
		$Option = 'Snap'
		$TraceParams.RecordingProfiles[0] = ".\Wprp\Memory.wprp!ReferenceSet"
		$ViewerParams.ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\VirtualAlloc.wpaProfile", ".\WPAP\ResidentSet.wpaProfile"
	}
	if ($Option)
	{
		$TraceParams.InstanceName += "#$Option" # eg: "MSO-Trace-Memory#Lite"
		$ViewerParams.TraceName = $TraceParams.InstanceName
		$Option = " -$Option"
	}

# ===== END CUSTOMIZE ====

# Main

	# Use Windows Performance Recorder.  It's much simpler, but requires Admin privileges.
	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS

	switch ($Result)
	{
	Started
	{
		if (!$Option -and $WPR_Flushable)
		{
			$Result = InvokeWPR -MarkerFlush Initial_ReferenceSet_Flush -InstanceName $TraceParams.InstanceName
			Write-Msg "Flushing System-wide RAM usage.`n"

			if ($Result) { Write-Status "MarkerFlush Result =`n$Result`n" }
		}
		$Command = GetScriptCommand
		Write-Msg "ETW Memory tracing has begun."
		Write-Msg "Exercise the application, then run: $Command Stop$Option [-WPA]"
		if (!$Option -or $Lean) { Write-Msg "(Do not quit the application until tracing has stopped!)" }
		Write-Msg "Other Options:"
		Write-Msg "$Command Status$Option [-verbose]"
		Write-Msg "$Command Cancel$Option"
		Write-Msg
	}
	Collected { WriteTraceCollected $TraceParams.InstanceName } # $WPA switch
	View      { $WPA = $True }
	Success   { $WPA = $False }
	Error     { exit 1 }
	}

	if ($WPA)
	{
		 # Resident Set graph requires: -KeepRundown (no: -cliprundown)
		if ($Snap) { LaunchViewer @ViewerParams -FastSym:$FastSym -ExtraParams:'-KeepRundown' }
		else       { LaunchViewer @ViewerParams -FastSym:$FastSym }
	}

exit 0 # Success
