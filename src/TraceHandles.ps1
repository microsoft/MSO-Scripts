<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	Kernel Handles, GDI and User Handles, Modules

	.DESCRIPTION

	.\TraceHandles Start [-Loop] [-CLR] [-JS]
	.\TraceHandles Stop [-WPA [-FastSym]]
	.\TraceHandles View [-Path <path>\MSO-Trace-Handles.etl|.wpapk] [-FastSym]
	.\TraceHandles Status
	.\TraceHandles Cancel
	    -Loop: Record only the last few minutes of activity (circular memory buffer). 
	    -CLR:  Resolve call stacks for C# (Common Language Runtime).
	    -JS:   Resolve call stacks for JavaScript.
	    -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	    -Path: Optional path to a previously collected trace.
	    -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	              See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	    -Verbose

	.LINK

	https://github.com/microsoft/MSO-Scripts/wiki/Handles
	https://learn.microsoft.com/en-us/windows/desktop/SysInfo/object-categories
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

	# "Optional path to a previously collected trace: MSO-Trace-Handles.etl"
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
			# This XML file contains tracing parameters organized by ProfileName.
			# To see the available profiles, run: wpr -profiles .\WPRP\Handles.wprp
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
		InstanceName = "MSO-Trace-Handles"
	}

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = ".\WPAP\BasicInfo.wpaProfile", ".\WPAP\Handles.wpaProfile"

		# The trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

# ===== END CUSTOMIZE ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = $PSScriptRoot
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"


# If it's an earlier version of the OS then it can't collect GDI/User handles.
[Version]$OSBuildForHandles = '10.0.18315'

# If it's an earlier version of WPA then it won't be able to show the GDI/User handles as well.
[Version]$WPABuildForHandles = '10.0.19600' # See ".\WPAP\Handles.19600.wpaProfile"


<#
	GDI/User Handle tracing is available only since Windows 10.0.18315.
#>
function WarnOnlyKernelHandles
{
	if (CheckOSVersion $OSBuildForHandles) { return }

	Write-Warn "Tracing only Kernel Object Handles: Process, Thread, Registry Key, File, etc."
	Write-Warn "To trace GDI and User Handles, a more recent version of Windows is required ($OSBuildForHandles+)."
	Write-Warn "See: https://learn.microsoft.com/en-us/windows/desktop/SysInfo/object-categories"
	Write-Warn
}


<#
	If this is a newer version of Windows but an older version the Windows Performance Analyzer (WPA) then warn.
#>
function WarnViewerForHandles
{
	if (!(CheckOSVersion $OSBuildForHandles)) { return }

	$WpaPath = GetWptExePath "wpa.exe"

	$VersionInfo = GetFileVersion $WpaPath

	if ($VersionInfo -lt $WPABuildForHandles)
	{
		Write-Warn "Warning: Windows Performance Analyzer (WPA) will show Kernel Object Handles,"
		Write-Warn "but only limited info on GDI and User Handles (without WPA version $WPABuildForHandles+)."
		Write-Warn "https://learn.microsoft.com/en-us/windows/desktop/SysInfo/object-categories"
		Write-Msg

		Write-Warn "Please check for a recent version here:"
		Write-Warn "`thttps://apps.microsoft.com/detail/9n0w1b2bxgnz"
		Write-Warn "Or set the WPT_PATH environment variable to the folder which contains a newer WPA.exe"
		Write-Msg
	}
}


# Main

	# Tracing Kernel handles is available in Windows 8.0 (v6.2) and above.

	if (!(CheckOSVersion '6.2.0')) { Write-Err "`nHandle tracing is available starting with Windows 8.0"; exit 1 }

	# Tracing GDI / User handles is available in Windows 10.0.18315 and above.

	$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS

	switch ($Result)
	{
	Started
		{
		Write-Msg "ETW Handle tracing has begun.`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n"
		WarnOnlyKernelHandles
		}
	Collected { WriteTraceCollected $TraceParams.InstanceName }
	View      { $WPA = $True }
	Success   { $WPA = $False }
	Error     { exit 1 }
	}

	if ($WPA) { WarnViewerForHandles; LaunchViewer @ViewerParams -FastSym:$FastSym }

exit 0 # Success
