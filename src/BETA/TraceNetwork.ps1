<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture an ETW trace of Network Activity
	and view it using the NetBlame WPA Add-in.

	.DESCRIPTION

	Trace Network activity.
	  TraceNetwork Start [Start_Options]
	  TraceNetwork Stop  [-WPA [-FastSym]]

	Trace Windows Restart: Network activity.
	  TraceNetwork Start -Boot [Start_Options]
	  TraceNetwork Stop  -Boot [-WPA [-FastSym]]

	  TraceNetwork View   [-Path <path>\MSO-Trace-Network.etl|.wpapk] [-FastSym]
	  TraceNetwork Status [-Boot]
	  TraceNetwork Cancel [-Boot]

	  -Boot: Trace Network activity during the next Windows Restart.
	  -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	  -Path: Optional path to a previously collected trace.
	  -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	            See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	  -Verbose

	Start_Options
	  -Loop: Record only the last few minutes of activity (circular memory buffer).
	  -CLR : Resolve call stacks for C# (Common Language Runtime).
	  -JS  : Resolve call stacks for JavaScript.

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

	# Trace Network activity during the next Windows Restart.
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

	# "Optional path to a previously collected trace: MSO-Trace-Network.etl"
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null,

	# "Faster symbol resolution by loading only from SymCache, not PDB"
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

$VersionMinForAddin = [Version]'11.7.383' # This public WPA version and later use SDK v1.2.2+
$VersionForProcessors = [Version]'11.8.0' # WPA Version 11.8.262 and later requires: -Processors (Earlier versions allow it.)

$AddInPaths = @(
	"$script:ScriptHomePath\ADDIN" # Released ZIP with NetBlame Plug-in
	"$script:ScriptRootPath\NetBlame\bin\*" # Cloned project with built NetBlame Plug-in
	)

$WPA_Version_RegPath = "$script:RegPathStatus\WPA-Plugin" # $RegPathStatus also used for SetProfileStartTime, etc.

# Most modern versions of WPA list/accept these plug-in names via: WPA -listplugins -addsearchdir MSO-Scripts\BETA\ADDIN
$ETW_Plugins_Default = 'Event Tracing for Windows','Office_NetBlame'


<#
	Find the path that contains the most recent (or 'Release') NetBlame plug-in: NetBlameAddIn.dll
	If this installation was downloaded and unzipped from the Release site, then it will be in: BETA\ADDIN
	If this installation was cloned and built then it will be in NetBlame\bin\Release|Debug\net8.0
#>
function GetAddinPath
{
	# List the various path(s) which contain NetBlameAddIn.dll

	$moduleName = 'NetBlameAddIn.dll'
	$paths = Resolve-Path -Path $AddInPaths -ErrorAction:SilentlyContinue
	$pathIOs = $paths | ForEach-Object { Get-ChildItem -Path $_ -Filter $moduleName -Recurse -ErrorAction:SilentlyContinue }

	if (!$pathIOs)
	{
		[string[]]$uPaths = ($paths.Path + $AddInPaths) | Sort-Object -Unique -Descending

		Write-Err "The `"NetBlame`" WPA Plug-in ($moduleName) was not be found at any of these locations:"
		foreach ($path in $uPaths) { Write-Err "`t$($path)" }

		Write-Info 'Please see: https://github.com/Microsoft/MSO-Scripts/wiki/Network-Activity#plugin'

		# No reason to go on!
		exit 1
	}

	# Choose the path which contains the most recent NetBlameAddIn.dll, or a recent 'Release' version.
	$pathIORel = $pathIOs | Where-Object { $_.FullName -like '*Release*' }
	if ($pathIORel) { $pathIOs = $pathIORel } # filter to 'Release' version(s)
	$pathIO = $pathIOs | Sort-Object { $_.LastWriteTime } -Descending | Select-Object -First 1
	$path = (Split-Path -Path $pathIO.FullName -Parent)

	Write-Status "NetBlame Plug-in found at: $path"
	return $path
}


<#
	Return a string array of WPA Plug-ins needed for this script.
	Usually: "Event Tracing for Windows","Office_NetBlame"
#>
function GetPluginsList
{
Param (
	[Version]$WpaVersion
)
	[string[]]$ETW_Plugins = $Null
	if ($WpaVersion)   { $ETW_Plugins = GetRegistryValue $WPA_Version_RegPath $WpaVersion.ToString() }
	if (!$ETW_Plugins) { $ETW_Plugins = $ETW_Plugins_Default }

	return $ETW_Plugins
}


<#
	Invoke: WPA -listplugins -addsearchdir MSO-Scripts\BETA\ADDIN
	Munge/filter the output to get its list of available plug-ins.
	If the list has changed, return $True after storing the result in the registry for: GetPluginsList
	On error, write out a debug string and return $False.
#>
function ResetPluginsList
{
Param (
	[string]$WpaPath,
	[string]$SearchDir,
	[Version]$WpaVersion
)
	Write-Status "Querying WPA for a list of available plug-ins."

	if (IsShellPath $WpaPath) { $WpaPath = ShimPathFromShellPath $WpaPath }

	# Query WPA for the list of available plug-ins.
	$WpaPath2 = $Null
	$timeStart = Get-Date
	$WpaListArgs = GetArgs -listplugins -addsearchdir $SearchDir
	$Result = InvokeExe $WpaPath @WpaListArgs
	if (!$Result -and ($global:LastExitCode -eq 329)) # ERROR_OPERATION_IN_PROGRESS
	{
		# Running a batch file process wrapper, so no result available.
		# Instead, find the path of the WPA process which just now launched.
		# Run that directly.

		$proc2 = GetRunningProcess 'WPA' $timeStart
		if ($proc2)
		{
			$WpaPath2 = $proc2.MainModule.FileName
			if ($WpaPath2)
			{
				$Result = InvokeExe $WpaPath2 @WpaListArgs
			}
		}
	}

	# $Result = verbose text to filter, eg.: " * Event Tracing for Windows" ...

	$Lines = $Null
	if ($Result -and !$global:LastExitCode)
	{
		$Lines = $Result.Split("`r?`n", [System.StringSplitOptions]::RemoveEmptyEntries)
		$Lines = $Lines | foreach-object { if ($_-match '\* (.*?)$') { $matches[1] } } # '  * Event Tracing for Windows', etc.
	}
	if (!$Lines -or !$Lines.Count) { Write-Dbg "'WPA $WpaListArgs' (v$WpaVersion) returned: `"$Result`""; return $False }

	# Filter to, eg.: 'Event Tracing for Windows (Internal)' 'XPerf' 'Office_NetBlame'

	$Filter = @( 'Event Tracing*', 'XPerf*', 'Office*' )
	$Plugins = $Lines | where { $_ | select-string -pattern $Filter }

	# Expecting exactly two plug-ins, similar to the default list.

	$PluginList = "WPA v$WpaVersion Plug-ins:`n$($Lines | Format-Table | Out-String)"
	if (!$Plugins -or ($Plugins.Count -ne $ETW_Plugins_Default.Count)) { Write-Dbg $PluginList; return $False }

	Write-Status $PluginList

	# Compare the queried plug-ins agains the default or previously registered plug-ins.
	# If there is no difference then return $False : nothing changed.

	$PluginsPrev = GetPluginsList $WpaVersion

	if ($PluginsPrev -and ($PluginsPrev.Count -ne 0) -and ($PluginsPrev.Count -eq $Plugins.Count))
	{
		[bool]$fSame = $True
		for ($i = 0; $i -lt $Plugins.Count; $i = $i + 1)
		{
			if ($Plugins[$i] -ne $PluginsPrev[$i]) { $fSame = $False; break }
		}
		if ($fSame) { return $False }
	}

	Write-Status "Resetting WPA Plug-in Names from [ $PluginsPrev ] to [ $Plugins ]."

	# The list of plug-ins is different from the default, or what was previously registered.

	SetRegistryValue $WPA_Version_RegPath $WpaVersion.ToString() 'MultiString' $Plugins

	if ($WpaPath2)
	{
		# The default 'version' of WPA.bat got registered with the plugins (above).
		# Now register the plugins using the _real_ version of WPA.exe, as invoked.

		$WpaVersion = GetFileVersion $WpaPath2
		if ($WpaVersion -ge $VersionForProcessors)
		{
			SetRegistryValue $WPA_Version_RegPath $WpaVersion.ToString() 'MultiString' $Plugins
		}
	}

	return $True
}


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

	if (!(CheckOSVersion '10.0.0'))
	{
		Write-Action "`nThis network analyzer plug-in will not likely work before Windows 10.`n"
	}

	# Find the most recent version of the viewer: Windows Performance Analyzer (WPA)

	$WPA = 'WPA.exe'
	$WpaPath = GetWptExePath $WPA
	$Version = GetFileVersion $WpaPath

	if ((IsRealVersion $Version) -and !$env:WPT_PATH)
	{
		# Find an even newer version, maybe.

		$WpaPath2 = GetWptExePath $WPA -Silent -NoShell -AltPath
		$Version2 = GetFileVersion $WpaPath2

		if ((IsRealVersion $Version2) -and ($Version2 -gt $Version))
		{
			$WpaPath = $WpaPath2
			$Version = $Version2
		}
	}

<#
	1. Found a recent WPA
	2. Found an old WPA
	3. Found WPA.bat or AppExecAlias shim file WPA.exe without 'real' version info
	4. Found no WPA (but try to launch "WPA" anyway)
#>
	if ((IsRealVersion $Version) -and ($Version -lt $VersionMinForAddin))
	{
		# Case 2: Do not proceed.

		Write-Err "Found an older version of the Windows Performance Analyzer (WPA): $Version"
		Write-Err "`"$WpaPath`""
		Write-Err "The minimum version for this analysis is: $VersionMinForAddin"
		WriteWPTInstallMessage "WPA.exe"
		exit 1
	}

	Write-Msg "Opening trace:" (ReplaceEnv $TraceFilePath)
	Write-Msg "Using the NetBlame Add-in."

	# The add-in resolves symbols. Don't also enable the main WPA symbol resolution mechanism.

	$ExtraParams = $Null
	$Processors = $Null

	if (!$Version -or !(IsRealVersion $Version) -or ($Version -ge $VersionForProcessors))
	{
		# Required for WPA.bat or WPA.exe v11.8+ to bypass the New WPA Launcher.
		$Processors = GetPluginsList $Version
		foreach ($Processor in $Processors)
		{
			$ExtraParams += GetArgs -processors "`"$Processor`""
		}
	}

	$AddInPath = GetAddinPath
	$ExtraParams += GetArgs -addsearchdir "`"$AddInPath`"" -NoSymbols

	# Now load LaunchViewerCommand and related.

	. "$ScriptRootPath\INCLUDE.WPA.ps1"

	$Process = LaunchViewerCommand $WpaPath $TraceFilePath $ViewerConfig $Version -FastSym:$FastSym $ExtraParams

	if ($Process)
	{
		$Process.Close()

		if ($global:LastExitCode -eq -1)
		{
			# Running WPA process but no main window: bad parameters!?

			if ($Processors -and (ResetPluginsList $WpaPath $AddInPath $Version))
			{
				Write-Warn
				Write-Warn "WPA did not launch."
				Write-Warn "The issue has been corrected with an updated list of WPA plug-ins."
				Write-Action "Please simply re-run the same command: $(GetScriptCommand) View ..."
			}
			else
			{
				Write-Err
				Write-Err "WPA aborted launch with these extra parameters: $ExtraParams"
				if (!(DoVerbose)) { Write-Err "For more info, please re-run with -Verbose: $(GetScriptCommand) View -Verbose ..." }
			}
		}
	}
} # LaunchViewerWithAddIn


# Main

$Result = ProcessTraceCommand $Command @TraceParams -Loop:$Loop -Boot:$Boot -CLR:$CLR -JS:$JS

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
