<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	CPU Samples, Thread Dispatch, Processes, Modules

	.DESCRIPTION

	Trace CPU and Thread Activity
	  TraceCPU Start [-Hang|-Lean|-Lite] [Start_Options]
	  TraceCPU Stop  [-WPA [-Hang] [-FastSym]]
	  TraceCPU View  [-Hang] [-Path <path>\MSO-Trace-CPU.etl|.wpapk] [-FastSym]

	Trace Windows Restart: CPU and Thread Activity
	  TraceCPU Start  -Boot [-Lean|-Lite] [Start_Options]
	  TraceCPU Stop   -Boot [-WPA [-FastSym]]
	  TraceCPU View  [-Path <path>\MSO-Trace-CPU.etl|.wpapk] [-FastSym]

	  TraceCPU Status [-Boot]
	  TraceCPU Cancel [-Boot]

	  -Lean: Restricted data collection: CPU samples only, no stackwalk.
	  -Lite: Reduced data collection: CPU samples only, with stackwalk.
	  -Hang: Reduced data collection: Stackwalk all threads at Start and Stop.
	         (May be slow to Start and to Stop with high system thread count.)
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

	https://github.com/microsoft/MSO-Scripts/wiki/CPU-and-Threads
	https://github.com/microsoft/MSO-Scripts/wiki/Analyze-Windows-Boot
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows
	https://learn.microsoft.com/en-us/shows/defrag-tools/39-windows-performance-toolkit
#>

[CmdletBinding(DefaultParameterSetName = "View")]
Param(
	# "Start, Stop, Status, Cancel, View"
	[Parameter(Position=0)]
	[string]$Command,

	# "Reduced data collection: CPU samples only, no stackwalk"
	[Parameter(ParameterSetName="Start")]
	[switch]$Lean,

	# "Reduced data collection: CPU samples only, with stackwalk"
	[Parameter(ParameterSetName="Start")]
	[switch]$Lite,

	# "Reduced data collection: Stackwalk hung/deadlocked threads at Start and Stop"
	[Parameter(ParameterSetName="Start")]
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$Hang,

	# "Record only the last few minutes of activity (circular memory buffer)."
	[Parameter(ParameterSetName="Start")]
	[switch]$Loop,

	# "Trace CPU activity during the next Windows Restart."
	[switch]$Boot,

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
			# Capture CPU Samples and Dispatcher Info with symbolic stackwalks for all processes.
			".\WPRP\CPU.wprp!CPU-DispatchEx" # Includes: Code Markers, HVAs, other light logging
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
		)

		# This is the arbitrary name of the tracing session/instance:
		InstanceName = "MSO-Trace-CPU"
	}

	$RecordingProfileLean = @( ".\WPRP\CPU.wprp!CPU-Lean" )  # Just this one recording profile with: -Lean

	$RecordingProfileLite = @( ".\WPRP\CPU.wprp!CPU-SampleOnly" )  # Just this one recording profile with: -Lite

	$RecordingProfileHang = @( ".\WPRP\CPU.wprp!Responsiveness" )  # Just this one recording profile with: -Hang

	$RecordingProfileFaster = ".\WPRP\CPU.wprp!CPU-Dispatch" # Replace the first recording profile for many threads on the system.

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\Defender.wpaProfile", ".\WPAP\CPU.wpaProfile"

		# The default trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

	if ($Hang) { $ViewerParams.ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\Threads.wpaProfile" }

# ===== END CUSTOMIZE ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = $PSScriptRoot
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"


function SystemThreadCount() { return Get-Process | Select-Object @{Name='ThreadCount';Expression={$_.Threads.Count}} | Measure-Object -sum -max ThreadCount }

<#
	Capturing stackwalks for ALL THREADS (rundown) may slow the WPR trace start and stop time,
	sometimes by minutes when there are thousands of threads in existence on the system.
	To mitigate that:
		In this registry key: HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\WMI\Trace
		Set this REG_DWORD value: StackCaptureTimeout (default value = 400 ms / stackwalk; smaller = faster)
		Then reboot the OS.

	Return $True if there are too many threads running on the system.
	The threshold is: 2500 (default, StackCaptureTimeout >= 400) up to 25000 (StackCaptureTimeout <= 40)
#>
function CheckSystemThreadCount
{
Param (
	[Microsoft.PowerShell.Commands.GenericMeasureInfo]$ThreadCount
)
	# When there are more than this many threads on the system, or any one process has this many / 5, then thread rundown stackwalk will be SLOW.
	$ThreadCountThresholdDft = 2500

	# The default value of StackCaptureTimeout is 400 ms
	$StackCaptureTimeoutDft = 400

	$StackCaptureTimeout = (GetRegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Control\WMI\Trace' 'StackCaptureTimeout') -as [int]

	if ($StackCaptureTimeout -and ($StackCaptureTimeout -ne 400))
	{
		Write-Warn "Warning: stackwalk capture timeout is set to $StackCaptureTimeout ms (default = 400 ms)"
		Write-Warn "https://github.com/microsoft/MSO-Scripts/wiki/CPU-and-Threads#deadlocks:~:text=StackCaptureTimeout"
	}

	if (!$StackCaptureTimeout -or ($StackCaptureTimeout -le 0) -or ($StackCaptureTimeout -ge $StackCaptureTimeoutDft))
	{
		$ThreadCountThreshold = $ThreadCountThresholdDft
	}
	elseif ($StackCaptureTimeout -le $StackCaptureTimeoutDft / 10)
	{
		$ThreadCountThreshold = $ThreadCountThresholdDft * 10
	}
	else # 40 < $StackCaptureTimeout < 400
	{
		$ThreadCountThreshold = [int]($ThreadCountThresholdDft * $StackCaptureTimeoutDft / $StackCaptureTimeout)
	}

	Write-Status "Total system threads: $($ThreadCount.Sum) / Max process threads: $($ThreadCount.Maximum)"

	return (($ThreadCount.Sum -ge $ThreadCountThreshold) -or ($ThreadCount.Maximum -ge $ThreadCountThreshold/5))
}


# Main

	# Change the base recording profile: Capture only 'Lean' or 'Lite' CPU Samples.
	# Or if there are LOTS of threads on the system, disable the thread rundown with stackwalk.

	if ($Command -eq "Start")
	{
		if ($Lean)
		{
			$TraceParams.RecordingProfiles = $RecordingProfileLean
		}
		elseif ($Lite)
		{
			$TraceParams.RecordingProfiles = $RecordingProfileLite
		}
		elseif ($Boot)
		{
			if ($Hang)
			{
				Write-Err "-Boot and -Hang are incompatible."
				exit 1
			}

			# No thread rundown during Windows restart.
			$TraceParams.RecordingProfiles[0] = $RecordingProfileFaster
		}
		elseif ($Loop)
		{
			# WPR doesn't do rundown in Memory mode. It would tend to age out of the circular buffer anyway.

			if ($Hang)
			{
				Write-Err "-Loop and -Hang are incompatible."
				exit 1
			}

			Write-Warn "-Loop: Disabling thread rundown with stackwalk."

			$TraceParams.RecordingProfiles[0] = $RecordingProfileFaster
		}
		elseif ($Hang)
		{
			$TraceParams.RecordingProfiles = $RecordingProfileHang

			$ThreadCount = SystemThreadCount

			if (CheckSystemThreadCount $ThreadCount)
			{
				Write-Warn "There are $($ThreadCount.Sum) threads on the system. Thread rundown `& stackwalk may be slow."
			}
		}
		else # (!$Lean -and !$Lite -and !$Loop -and !$Hang -and !Boot)
		{
			$ThreadCount = SystemThreadCount

			if (CheckSystemThreadCount $ThreadCount)
			{
				Write-Warn "There are $($ThreadCount.Sum) threads on the system. Disabling thread rundown with stackwalk."
				Write-Warn "https://github.com/microsoft/MSO-Scripts/wiki/CPU-and-Threads#deadlocks:~:text=StackCaptureTimeout"

				$TraceParams.RecordingProfiles[0] = $RecordingProfileFaster
			}
			else
			{
				Write-Status "Enabling thread rundown with stackwalk."

				# Already set: $TraceParams.RecordingProfiles
			}
		}
	} # "Start"

	# Use Windows Performance Recorder (WPR).  It's much simpler, but requires Admin privileges.

	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -Boot:$Boot -CLR:$CLR -JS:$JS

	switch ($Result)
	{
	Started
	{
		Write-Msg "ETW CPU tracing has begun."

		if ($Lean -or $Lite)
		{
			Write-Msg "CPU Sampling mode:"
			Write-Msg -NoNewline (InvokeWPR -ProfInt)
			Write-Info "NOTE: You can adjust the CPU sampling interval by running:"
			Write-Info "`tWPR -SetProfInt [interval: ms/10000]"
			Write-Msg
		}

		$HangSwitch = Ternary $Hang "-WPA -Hang" "-WPA"
		Write-Msg "Exercise the application, then run: $(GetScriptCommand) Stop [$HangSwitch]"
	}
	Collected { $Null = _WriteTraceCollectedExtra $ViewerParams.TraceName $(Ternary $Hang "-Hang" $Null) } # $WPA switch
	View      { $WPA = $True }
	Success   { $WPA = $False }
	Error     { exit 1 }
	}

	if ($WPA) { LaunchViewer @ViewerParams -FastSym:$FastSym }

exit 0 # Success
