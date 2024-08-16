<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	CPU Samples, Thread Dispatch, File I/O, Office Logging Providers, ThreadPool, Processes, Modules

	.DESCRIPTION

	.\TraceMondo Start [-Loop] [-Lean] [-CLR] [-JS]
	.\TraceMondo Stop [-WPA [-Lean] [-FastSym]]
	.\TraceMondo View [-Path <path>\MSO-Trace-Mondo.etl|.wpapk] [-Lean] [-FastSym]
	.\TraceMondo Status
	.\TraceMondo Cancel
	  -Loop: Record only the last few minutes of activity (circular memory buffer).
	  -Lean: Reduced provider set; fewer view profiles.
	  -CLR:  Resolve call stacks for C# (Common Language Runtime).
	  -JS:   Resolve call stacks for JavaScript.
	  -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	  -Path: Optional path to a previously collected trace.
	  -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	            See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	  -Verbose

	.LINK

	https://github.com/microsoft/MSO-Scripts/wiki/CPU-and-Threads
	https://github.com/microsoft/MSO-Scripts/wiki/File-and-Disk-IO
	https://github.com/microsoft/MSO-Scripts/wiki/Handles
	https://github.com/microsoft/MSO-Scripts/wiki/Network-Activity
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

	# "Support JavaScript"
	[Parameter(ParameterSetName="Start")]
	[switch]$JS,

	# "Launch WPA after collecting the trace"
	[Parameter(ParameterSetName="Stop")]
	[switch]$WPA,

	# Reduced provider set
	[switch]$Lean,

	# "Optional path to a previously collected trace: MSO-Trace-Mondo.etl"
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
			".\WPRP\CPU.wprp!CPU-Dispatch"
			".\WPRP\Defender.wprp!AntiMalware.Verbose"
			".\WPRP\FileDiskIO.wprp!FileAndDiskIO"
			".\WPRP\OfficeProviders.wprp!OfficeLogging"
			".\WPRP\WindowsProviders.wprp!TuttiFrutti"
			".\WPRP\Handles.wprp!AllHandles"
			".\WPRP\Network.wprp!NetworkMain" # or Network.15002.wprp - See ReadMe.txt
			".\WPRP\ThreadPool.wprp!ThreadPool" # or ThreadPool.15002.wprp - See ReadMe.txt

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
			".\OETW\MsoEtwTP.man"
			".\OETW\MsoEtwDQ.man"
			".\OETW\MsoEtwAS.man"
		)

		# This is the arbitrary name of the tracing session/instance:
		InstanceName = "MSO-Trace-Mondo"
	}

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig =
		@(
			".\WPAP\BasicInfo.wpaProfile"
			".\WPAP\Defender.wpaProfile",
			".\WPAP\Handles.wpaProfile"
			".\WPAP\DiskIO.wpaProfile"
			".\WPAP\FileIO.wpaProfile"
			".\WPAP\CPU.wpaProfile"
			".\WPAP\ETW-Overhead.wpaProfile" # Empty but for StackTags (last = top priority)
		)

		# The default trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

	if ($Lean)
	{
		# Reduced provider set:
		$TraceParams.RecordingProfiles =
		@(
			".\WPRP\CPU.wprp!CPU-Dispatch"
			".\WPRP\Defender.wprp!AntiMalware.Light"
			".\WPRP\FileDiskIO.wprp!FileAndDiskIO-Lean"
		)

		# Correspondingly reduced provider manifests:
		$TraceParams.ProviderManifests =
		@(
			# None
		)

		# Correspondingly reduced view profiles:
		$ViewerParams.ViewerConfig =
		@(
			".\WPAP\BasicInfo.wpaProfile"
			".\WPAP\Defender.wpaProfile",
			".\WPAP\DiskIO.wpaProfile"
			".\WPAP\FileIO.wpaProfile"
			".\WPAP\CPU.wpaProfile"
			".\WPAP\ETW-Overhead.wpaProfile" # Empty but for StackTags (last = top priority)
		)
	}

# ===== END CUSTOMIZE ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = $PSScriptRoot
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"

# Main

	# Use Windows Performance Recorder.  It's much simpler, but requires Admin privileges.

	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS

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

		if (!$Lean)
		{
			Write-Warn "`nWarning: Many ETW providers were enabled in this trace, and the CPU overhead can be significant."
			Write-Warn "This is especially true when there is substantial Registry or File I/O traffic."
			Write-Warn "To expose the ETW Overhead within WPA's CPU Samples, enable the `"Stack Tag`" column."

			Write-Info "`nAlso view network activity with:"
			Write-Info "BETA\TraceNetwork View -Path `"$(GetTraceFilePathString $TraceParams.InstanceName)`""
		}
	}

exit 0 # Success
