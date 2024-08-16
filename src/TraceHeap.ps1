<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	Track each Windows Heap alloc/free of the specified process.
	Or (-Lean) track VirtualAlloc (upon which the Windows Heap is built).

	.DESCRIPTION

	.\TraceHeap Start -EXE Name.exe[,Name2.exe...] [-Loop] [-Snap [-Lean]] [-CLR] [-JS]
	.\TraceHeap Start -ProcessID 1234 [-Loop] [-Snap [-Lean]] [-CLR] [-JS]
	.\TraceHeap Start -Lean [-Loop] [-CLR] [-JS]
	.\TraceHeap Stop [-Snap] [-WPA [-FastSym] [-Lean]]
	.\TraceHeap View [-Path <path>\MSO-Trace-Heap.etl|.wpapk] [-Snap] [-Lean] [-FastSym]
	.\TraceHeap Status [-Snap]
	.\TraceHeap Cancel [-Snap]
	    -EXE:  Trace Windows Heap allocations in a future process: Name.exe
	    -ProcessID: Trace Windows Heap allocations in a running process with this PID.
	    -Snap: Capture a Windows Heap Snapshot every 5 seconds, or every 5 minutes (-Lean).
	    -Lean: Trace committed memory allocated via VirtualAlloc. (Windows Heap is built on VirtualAlloc).
	    -Loop: Record only the last few minutes of activity (circular memory buffer). 
	    -CLR:  Resolve call stacks for C# (Common Language Runtime).
	    -JS:   Resolve call stacks for JavaScript.
	    -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	    -Path: Optional path to a previously collected trace.
	    -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	              See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	    -Verbose

	.LINK

	https://github.com/microsoft/MSO-Scripts/wiki/Windows-Heap
	https://learn.microsoft.com/en-us/windows/win32/memory/heap-functions
	https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/record-heap-snapshot
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows
	https://learn.microsoft.com/en-us/shows/defrag-tools/39-windows-performance-toolkit
#>

[CmdletBinding(DefaultParameterSetName="View")]
Param(
	# Start, Stop, View, Status, Cancel
	[Parameter(Position=0)]
	[string]$Command,

	# PID of currently running process to trace
	[Parameter(ParameterSetName="StartPID", Mandatory=$True)]
	[int]$ProcessID = 0,

	# Name of future process to trace: Excel.exe
	[Parameter(ParameterSetName="StartEXE", Mandatory=$True)]
	[string[]]$EXE = $Null,

	# Capture a Windows Heap Snapshot every 5 seconds, or 5 minutes (-Lean).
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$Snap,

	# Trace/View only VirtualAlloc(MEM_COMMIT)
	[Parameter(ParameterSetName="StartLean", Mandatory=$True)]
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$Lean,

	# Record only the last few minutes of activity (circular memory buffer).
	[Parameter(ParameterSetName="StartLean")]
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[switch]$Loop,

	# Resove call stacks for C# / Common Language Runtime
	[Parameter(ParameterSetName="StartLean")]
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[switch]$CLR,

	# Resolve calls tacks for JavaScript
	[Parameter(ParameterSetName="StartLean")]
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[switch]$JS,

	# Launch the WPA Viewer with the collected trace
	[Parameter(ParameterSetName="Stop")]
	[switch]$WPA,

	# "Optional path to a previously collected trace: MSO-Trace-Heap.etl"
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null,

	# "Faster symbol resolution by loading only from SymCache, not PDB"
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$FastSym

	# [switch]$Verbose # implicit
)

if (!$EXE) { $EXE = $Null } # PSv2

# ===== CUSTOMIZE THIS =====

	$TraceParams =
	@{
		RecordingProfiles =
		@(
			# Trace only VirtualAlloc. (The Windows Heap acquires memory via VirtualAlloc.)
			".\WPRP\Heap.wprp!TraceVirtualAlloc" # Or Heap.wprp!TraceHeap_ByPID/Name
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
		InstanceName = "MSO-Trace-Heap"
	}

	# Change the base recording profile:
	if ($Snap)
	{
		# Heap tracing is by snapshot, using either app name (at launch) or process ID (a running process).
		$TraceParams.RecordingProfiles[0] = ".\WPRP\Heap.wprp!Heap_Snapshot"
		# Signal the use of the -Snap param by appending #Snap. See GetExtraParamFromInstance
		$TraceParams.InstanceName = "$($TraceParams.InstanceName)#Snap"
	}
	elseif ($ProcessID)
	{
		# Heap tracing is by process ID (a running process) and begins immediately.
		$TraceParams.RecordingProfiles[0] = ".\WPRP\Heap.wprp!TraceHeap_ByPID"
	}
	elseif (!$Lean)
	{
		# Heap tracing is by app name and begins at app launch, not on the fly.
		$TraceParams.RecordingProfiles[0] = ".\WPRP\Heap.wprp!TraceHeap_ByName"
	}

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\VirtualAlloc.wpaProfile"

		# The trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

	# Only show the "Heap" tabs when they might have data.
	if ($Snap) { $ViewerParams.ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\HeapSnapshot.wpaProfile" }
	elseif (!$Lean) { $ViewerParams.ViewerConfig += ".\WPAP\Heap.wpaProfile" }

# ===== END CUSTOMIZE ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = $PSScriptRoot
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"
. "$ScriptRootPath\INCLUDE.Heap.ps1"

# Main

if ($Snap)
{
	$Result = [ResultValue]::Success

	$Result = PrepareHeapSnapshotCommand $Command -ProcessId:$ProcessID -EXEs:([ref]$EXE) -InstanceName:$TraceParams.InstanceName

	if ($Result -eq [ResultValue]::Success)
	{
		$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS
	}

	switch ($Result)
	{
	Started
	{
		$Interval = 5 # seconds
		if ($Lean) { $Interval = 5 * 60 } # minutes
		$PostResult = PostProcessSnapshotCommand $Command -ProcessID:([ref]$ProcessID) -EXEs:$EXE $Interval $TraceParams.InstanceName
		switch ($PostResult)
		{
		Started { Write-Action "Now capturing Windows Heap snapshots every $Interval seconds via ETW for: $EXE [$ProcessID]`nExercise the application." }
		Success { Write-Action "To capture Windows Heap snapshots, launch and exercise: $EXE" }
		Error   { Write-Err "Unrecognized command!"; exit 1 }
		}
		Write-Action "Then run: $(GetScriptCommand) Stop -Snap [-WPA]"
		Write-Msg "`nOther Options:"
		Write-Msg "$(GetScriptCommand) Cancel -Snap"
		Write-Msg "$(GetScriptCommand) Status -Snap"
	}
	Error     { PrepareHeapSnapshotCommand "Cancel" -InstanceName:$TraceParams.InstanceName >$Null; exit 1 }
	Collected { WriteTraceCollected $TraceParams.InstanceName } # $WPA switch
	Success   { $WPA = $False }
	View      { $WPA = $True }
	}

	if ($WPA)
	{
		Write-Action "To view a difference of heap snapshots: Right-click within the table and choose: Diff View"
		LaunchViewer @ViewerParams -FastSym:$FastSym
	}	
}
else
{
	# Use Windows Performance Recorder.  It's much simpler, but requires Admin privileges.

	$Result = [ResultValue]::Success

	if (!$Lean)
	{
		$Result = PrepareHeapTraceCommand $Command -TraceParams:$TraceParams -ProcessID:$ProcessID -EXEs:([ref]$EXE)
	}

	if ($Result -eq [ResultValue]::Success)
	{
		$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS
	}

	switch ($Result)
	{
	Started
		{
		if ($Lean) { Write-Action "To trace committed memory allocations, exercise your scenario.`nThen run: $(GetScriptCommand) Stop [-WPA]`n" }
		elseif ($ProcessID) { Write-Action "Now tracing Windows Heap allocations via ETW for: $EXE`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n" }
		elseif ($EXE) { Write-Action "To trace heap allocations, launch and exercise: $EXE`nThen run: $(GetScriptCommand) Stop [-WPA]`n" }
		else { Write-Err "Unrecognized command!"; exit 1 }
		}
	Error     { PrepareHeapTraceCommand "Cancel" >$Null; exit 1 }
	Collected { WriteTraceCollected $TraceParams.InstanceName } # $WPA switch
	Success   { $WPA = $False }
	View      { $WPA = $True }
	}

	if ($WPA) { LaunchViewer @ViewerParams -FastSym:$FastSym }	
}

exit 0 # Success
