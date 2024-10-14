<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	Track each heap alloc/free of the specified process.

	.DESCRIPTION

	.\TraceHeapEx Start -EXE Name.exe[,Name2.exe...] [-Loop] [-CLR] [-JS]
	.\TraceHeapEx Start -ProcessID 1234 [-Loop] [-CLR] [-JS]
	.\TraceHeapEx Start -Lean [-Loop] [-CLR] [-JS]
	.\TraceHeapEx Stop [-WPA]
	.\TraceHeapEx View [-Path <path>\MSO-Trace-HeapX.etl]
	.\TraceHeapEx Status
	.\TraceHeapEx Cancel
	    -EXE:  Trace Windows Heap allocations in a future process: Name.exe
	    -ProcessID: Trace Windows Heap allocations in a running process with this PID.
	    -Lean: Trace committed memory allocated via VirtualAlloc. (Windows Heap is built on VirtualAlloc).
	    -Loop: Record only the last few minutes of activity (circular memory buffer). 
	    -CLR:  Resolve call stacks for C# (Common Language Runtime).
	    -JS:   Resolve call stacks for JavaScript.
	    -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	    -Path: Optional path to a previously collected trace.
	    -Verbose

	These scripts work with pre-Win10 versions of the Windows Performance Toolkit (WPT).

	.LINK

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
	[Parameter(ParameterSetName="StartPID")]
	[int]$ProcessID = 0, # = $Null for other parameter set in PSv2

	# Name of future process to trace: Excel.exe
	[Parameter(ParameterSetName="StartEXE")]
	[string[]]$EXE = $Null,

	# Trace only Handles and VirtualAlloc(MEM_COMMIT)
	[Parameter(ParameterSetName="StartLean")]
	[switch]$Lean,

	# Record only the last few minutes of activity (circular memory buffer).
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[Parameter(ParameterSetName="StartLean")]
	[switch]$Loop,

	# Resove call stacks for C# / Common Language Runtime
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[Parameter(ParameterSetName="StartLean")]
	[switch]$CLR,

	# Resolve calls tacks for JavaScript
	[Parameter(ParameterSetName="StartPID")]
	[Parameter(ParameterSetName="StartEXE")]
	[Parameter(ParameterSetName="StartLean")]
	[switch]$JS,

	# Launch the WPA Viewer with the collected trace
	[Parameter(ParameterSetName="Stop")]
	[switch]$WPA,

	# "Optional path to a previously collected trace: MSO-Trace-HeapX.etl"
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
			# Heap tracing is by app name and begins at app launch, not on the fly.
			".\WPRP\Heap.wprp.Verbose"
			".\WPRP\Handles.wprp.Light"
			".\WPRP\OfficeProviders.wprp.Light" # Code Markers
		<#
			^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ReadMe.txt

			"Registry" # Built-in
			".\WPRP\FileDiskIO.wprp.Light"
			"c:\MyProfiles\MyRecordingProfile.wprp"
		#>
		)

		# This is the arbitrary name of the tracing session/instance.
		InstanceName = "MSO-Trace-HeapX"
	}

	# Change the base recording profile:
	# Heap tracing is by process ID (a running process) and begins immediately.
	if ($ProcessID) { $TraceParams.RecordingProfiles[0] = ".\WPRP\Heap.wprp.Light" }
	# Trace only VirtualAlloc. (The Windows Heap acquires memory via VirtualAlloc.)
	if ($Lean) { $TraceParams.RecordingProfiles[0] = ".\WPRP\VirtualAlloc.wprp.Light" }

	$ViewerParams =
	@{
		# This configuration file organizes the data in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\Handles.wpaProfile", ".\WPAP\Heap.wpaProfile"

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
. "$ScriptRootPath\INCLUDE.Heap.ps1"

# Main

	$Result = [ResultValue]::Success

	if (!$Lean)
	{
		# TraceHeapEx.bat calls: PowerShell -file TraceHeapEx.ps1 %*
		# This doesn't interpret comma-separated array elements on input.
		# The alternative is: PowerShell -command TraceHeapEx.ps1 %*
		# But this doesn't preserve quoted paths.
		if ($EXE)
		{
			if ($EXE.Count -eq 1) { $EXE = $EXE[0].Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) }
		}
		else
		{
			$EXE = $Null # for [ref]$EXE in PSv2
		}

		$Result = PrepareHeapTraceCommand $Command -TraceParams:$TraceParams -ProcessID:$ProcessID -EXEs:([ref]$EXE)
		ValidateResult "PrepareTraceHeapCommand" $Result
	}

	if ($Result -eq [ResultValue]::Success)
	{
		$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS
		ValidateResult "ProcessTraceCommand" $Result
	}

	switch ($Result)
	{
	Started
		{
		# Kernel Handle traces are available in Windows 8.0 (v6.2) and above.
		# Call stacks with symbols for VirtualAlloc traces are available in Windows 8.0 (v6.2) and above. (Heap traces do collect call stacks.)
		if (!(CheckOSVersion '6.2.0'))
		{
			Write-Warn "Warning: Handle traces and stackwalks for VirtualAlloc traces are available in Windows 8.0+`n"
		}

		if ($Lean) { Write-Msg "To trace committed memory allocations, exercise your scenario.`nThen run: $(GetScriptCommand) Stop [-WPA]`n" }
		elseif ($ProcessID) { Write-Msg "Now tracing ETW Heap allocations for: $EXE`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n" }
		elseif ($EXE) { Write-Msg "To trace heap allocations, launch and exercise: $EXE`nThen run: $(GetScriptCommand) Stop [-WPA]`n" }
		else { Write-Err "Unrecognized command!" }
		}
	Error
		{
		PrepareHeapTraceCommand "Cancel" >$Null
		exit 1
		}
	Collected { WriteTraceCollected $TraceParams.InstanceName; if ($WPA) { LaunchViewer @ViewerParams } }
	View      { LaunchViewer @ViewerParams }
	}

exit 0 # Success
