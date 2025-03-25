<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	Track each Windows Heap alloc/free of the specified process.
	Or (-Lean) track VirtualAlloc (upon which the Windows Heap is built).
	Also track Kernel, User, GDI handles.

	.DESCRIPTION

	Trace Heap Activity by Process Name or by Process ID.
	  TraceHeapEx Start -EXE Name.exe[,Name2.exe...] [Start_Options]
	  TraceHeapEx Start -ProcessID 1234 [Start_Options]
	  TraceHeapEx Stop [-WPA [-FastSym]]
	  TraceHeapEx View [-Path <path>\MSO-Trace-Heap.etl|.wpapk] [-FastSym]

	Trace Virtual Memory / Windows Heap allocations via VirtualAlloc.
	  TraceHeapEx Start -Lean [Start_Options]
	  TraceHeapEx Stop [-WPA -Lean [-FastSym]]
	  TraceHeapEx View  -Lean [-Path <path>\MSO-Trace-Heap.etl|.wpapk] [-FastSym]

	Trace Windows Restart: Heap Activity by Process Name.
	  TraceHeapEx Start -Boot -EXE Name.exe[,Name2.exe...] [Start_Options]
	  TraceHeapEx Stop  -Boot [-WPA [-FastSym]]
	  TraceHeapEx View        [-Path <path>\MSO-Trace-Heap.etl|.wpapk] [-FastSym]

	Trace Windows Restart: Virtual Memory / Windows Heap allocations via VirtualAlloc.
	  TraceHeapEx Start -Boot -Lean [Start_Options]
	  TraceHeapEx Stop  -Boot [-WPA -Lean [-FastSym]]
	  TraceHeapEx View        -Lean [-Path <path>\MSO-Trace-Heap.etl|.wpapk] [-FastSym]

	  TraceHeapEx Status [-Snap] [-Boot]
	  TraceHeapEx Cancel [-Snap] [-Boot]

	  -EXE : Trace Windows Heap allocations in a future process: Name.exe
	  -ProcessID: Trace Windows Heap allocations in a running process with this PID.
	  -Lean: Trace Committed Virtual Memory allocated via VirtualAlloc. (Windows Heap is built on VirtualAlloc).
	  -Boot: Trace CPU activity during the next Windows Restart.
	  -WPA : Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	  -Path: Optional path to a previously collected trace.
	  -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	              See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	  -Verbose

	Start_Options
	  -Loop: Record only the last few minutes of activity (circular memory buffer).
	  -CLR : Resolve symbolic stackwalks for C# (Common Language Runtime).
	  -JS  : Resolve symbolic stackwalks for JavaScript.

	.LINK

	https://github.com/microsoft/MSO-Scripts/wiki/Windows-Heap
	https://github.com/microsoft/MSO-Scripts/wiki/Handles
	https://learn.microsoft.com/en-us/windows/win32/memory/heap-functions
	https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc
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

	# Trace/View only Handles and VirtualAlloc(MEM_COMMIT)
	[Parameter(ParameterSetName="StartLean", Mandatory=$True)]
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$Lean,

	# Record only the last few minutes of activity (circular memory buffer).
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[Parameter(ParameterSetName="StartLean")]
	[switch]$Loop,

	# Trace CPU activity during the next Windows Restart.
	[switch]$Boot,

	# Resove symbolic stackwalks for C# / Common Language Runtime
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[Parameter(ParameterSetName="StartLean")]
	[switch]$CLR,

	# Resolve symbolic stackwalks for JavaScript
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[Parameter(ParameterSetName="StartLean")]
	[switch]$JS,

	# Launch the WPA Viewer with the collected trace
	[Parameter(ParameterSetName="Stop")]
	[switch]$WPA,

	# "Optional path to a previously collected trace: MSO-Trace-HeapX.etl"
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
			".\WPRP\Handles.wprp!AllHandles"
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
		InstanceName = "MSO-Trace-HeapX"
	}

	# Change the base recording profile:
	if ($ProcessID)
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
		ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\Handles.wpaProfile", ".\WPAP\VirtualAlloc.wpaProfile"

		# The trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

	# Only show the "Heap" tabs when they might have data.
	if (!$Lean) { $ViewerParams.ViewerConfig += ".\WPAP\Heap.wpaProfile" }

# ===== END CUSTOMIZE ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = $PSScriptRoot
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"
. "$ScriptRootPath\INCLUDE.Heap.ps1"

# Main

	if ($Boot -and $ProcessID)
	{
		Write-Err "-Boot and -ProcessID are incompatible."
		Write-Err "Run: $(GetScriptCommand) -?"
		exit 1
	}

	$Result = [ResultValue]::Success

	if (!$Lean)
	{
		$Result = PrepareHeapTraceCommand $Command -TraceParams:$TraceParams -ProcessID:$ProcessID -EXEs:([ref]$EXE) -Boot:$Boot
	}

	if ($Result -eq [ResultValue]::Success)
	{
		$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -Boot:$Boot -CLR:$CLR -JS:$JS
	}

	switch ($Result)
	{
	Started
		{
		if ($Lean) { Write-Action "To trace Windows Handles and Virtual Memory allocations, exercise your scenario.`nThen run: $(GetScriptCommand) Stop [-WPA]`n" }
		elseif ($ProcessID) { Write-Action "Now tracing Windows Handles and Heap allocations for: $EXE`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n" }
		elseif ($EXE) { Write-Action "To trace heap allocations, launch and exercise: $EXE`nThen run: $(GetScriptCommand) Stop [-WPA]`n" }
		else { Write-Err "Unrecognized command!"; exit 1 }
		}
	Error     { PrepareHeapTraceCommand "Cancel" >$Null; exit 1 }
	Collected { WriteTraceCollected $TraceParams.InstanceName } # $WPA switch
	Success   { $WPA = $False }
	View      { $WPA = $True }
	}

	if ($WPA) { LaunchViewer @ViewerParams -FastSym:$FastSym }

exit 0 # Success
