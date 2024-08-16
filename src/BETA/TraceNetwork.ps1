<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace:
	Network Activity

	.DESCRIPTION

	.\TraceNetwork Start [-Loop] [-CLR] [-JS]
	.\TraceNetwork Stop [-WPA [-FastSym]]
	.\TraceNetwork View [-Path <path>\MSO-Trace-Network.etl|.wpapk] [-FastSym]
	.\TraceNetwork Status
	.\TraceNetwork Cancel
	    -Loop: Record only the last few minutes of activity (circular memory buffer).
	    -CLR:  Resolve call stacks for C# (Common Language Runtime).
	    -JS:   Resolve call stacks for JavaScript.
	    -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	    -Path: Optional path to a previously collected trace.
	    -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	              See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	    -Verbose

	.LINK

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

	# "Optional path to a previously collected trace: MSO-Trace-Network.etl"
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null,

	# "Load symbols via SymCache only, not PDB."
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$FastSym

	# [switch]$Verbose # implicit
)

# ===== MODIFY THIS =====

	$TraceParams =
	@{
		RecordingProfiles =
		@(
			# This XML file contains tracing parameters organized by ProfileName.
			# To see the available profiles, run: wpr -profiles ..\WPRP\Network.wprp
			"..\WPRP\Network.wprp!NetworkFull" # or Network.15002.wprp - See ..\ReadMe.txt
			"..\WPRP\EdgeChrome.wprp!MSEdge_Basic" # or EdgeChrome.15002.wprp
			"..\WPRP\OfficeProviders.wprp!CodeMarkers" # Code Markers, HVAs, other light logging

		<#
			^^^ The first entry is the base recording profile for this script.
			vvv Additional recording profile string(s) follow. See ..\ReadMe.txt

			"Registry" # Built-in
			"..\WPRP\FileDiskIO.wprp!FileIO"
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
			# See: ..\OETW\ReadMe.txt and .\OETW\ReadMe.txt
			".\OETW\EdgeETW.man"
			".\OETW\ChromeETW.man"
			"..\OETW\MsoEtwTP.man" # Office Task Pool
			"..\OETW\MsoEtwCM.man" # Office Idle Manager
			"..\OETW\MsoEtwDQ.man" # Office Dispatch Queue
		)

		# This is the arbitrary name of the tracing session/instance.
		InstanceName = "MSO-Trace-Network"
	}

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = "..\WPAP\BasicInfo.wpaProfile", "..\WPAP\EdgeRegions.wpaProfile", ".\WPAP\Network.wpaProfile"

		# The trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

# ===== END MODIFY ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = "$PSScriptRoot\.."
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"

$VersionMinForAddin = [Version]'11.0.7' # This version and later use SDK v1.0.7+

$AddInPath = "$script:ScriptHomePath\ADDIN" # Uses SDK v1.0.7+

<#
	Setup and launch the Windows Performance Analyzer.
#>
function LaunchViewerWithAddIn
{
Param ( # $ViewerParams 'parameter splat'

	# Trace file name will be: $TraceName.ETL
	[Parameter(Mandatory=$true)] [string]$TraceName,

	# Optional: Viewer Configuration Files (.wpaProfile)
	[string[]]$ViewerConfig,

	# Optional, alternate path to trace file which overrides $TraceName.
	[string]$TraceFilePath,

	# Load symbols via SymCache only, not PDB.
	[switch]$FastSym
)
	# Get the full path of the trace file: .ETL or .WPAPK
	# The current directory may get changed below.

	EnsureTracePath

	if (!$TraceFilePath)
	{
		$TraceFilePath = GetTraceFilePathString $TraceName
		TestTraceFilePath $TraceFilePath
	}
	else
	{
		TestTraceFilePath $TraceFilePath
		$TraceFilePath = Convert-Path -Path (Resolve-Path -LiteralPath $TraceFilePath)
	}

	if ((GetProcessorArch) -eq "x86")
	{
		Write-Warn "`nThe Windows Performance Analyzer (WPA) must run on a 64-bit OS."
		exit 1
	}

	# The viewer: Windows Performance Analyzer (WPA)
	$WpaPath = GetWptExePath "WPA.exe"
	$Version = GetFileVersion $WpaPath

	if (!$Version -or ($Version -lt $VersionMinForAddin))
	{
		if ($Version)
		{
			Write-Err "Found an older version of the Windows Performance Analyzer (WPA): $Version"
		}
		elseif ($WpaPath -ne 'WPA')
		{
			Write-Err "Found an unknown version of the Windows Performance Analyzer (WPA)."
		}
		Write-Err "The minimum required version is: $VersionMinForAddin"

		if ($WpaPath -ne 'WPA') { WriteWPTInstallMessage "wpa.exe" } # Else GetWptExePath wrote the message

		if ($Version) { exit 1 } # else maybe we found WPA.bat, etc.
	}

	if (!(Test-Path -PathType container -Path $AddInPath -ErrorAction:SilentlyContinue))
	{
		Write-Err "Could not find the NetBlame add-in path: $AddInPath"
		exit 1
	}

	Write-Msg "Opening trace:" (ReplaceEnv $TraceFilePath)
	Write-Msg "Using the NetBlame Add-in."

	# Now load LaunchViewerCommand and related.

	. "$ScriptRootPath\INCLUDE.WPA.ps1"

	$ExtraParams = GetArgs -addsearchdir $AddInPath

	if ($FastSym)
	{
	<#	When using -addsearchpath <AddIn_Folder>, WPA doesn't accept: -symbols -symcacheonly
		Therefore, the NetBlame add-in recognizes -symcacheonly via environment variables:
		_NT_SYMBOL_PATH=<Empty>; _NT_SYMCACHE_PATH=<Paths>
	#>
		$Env:_NT_SYMBOL_PATH = $Null
	}

	$ExtraParams += GetArgs -NoSymbols # The add-in resolves symbols. Don't also enable the main WPA symbol resolution mechanism.

	$Result = LaunchViewerCommand $WpaPath $TraceFilePath $ViewerConfig $Version -FastSym:$FastSym $ExtraParams

} # LaunchViewerWithAddIn


# Main

# Use Windows Performance Recorder (WPR).

$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -CLR:$CLR -JS:$JS

switch ($Result)
{
Started   { Write-Msg "ETW Network tracing has begun.`nExercise the application, then run: $(GetScriptCommand) Stop [-WPA]`n" }
Collected { WriteTraceCollected $TraceParams.InstanceName } # $WPA switch
View      { $WPA = $True }
Success   { $WPA = $False }
Error     { exit 1 }
}

if ($WPA) { LaunchViewerWithAddIn @ViewerParams -FastSym:$FastSym }

exit 0 # Success
