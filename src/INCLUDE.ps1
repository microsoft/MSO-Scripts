# if ($Host.Version.Major -gt 2) { Set-StrictMode -version latest }

<#
	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	ENVIRONMENT VARIABLES (optional):
	WPT_PATH : Path/folder of the Windows Performance Toolkit containing WPA.exe, WPR.exe, etc.
	WPT_WPRP : Additional WPR Profile file paths (*.wprp), semi-colon separated.
	WPT_XPERF : Additional ETW providers in plus-separated, XPERF -ON format: NameOrGUID:KeywordFlags:Level:'$Stack'[+...]
	WPT_MODE : Special tracing mode - Shutdown
	TRACE_PATH : Path which receives generated traces and intermediate files. (Default = $Env:LocalAppData\MSO-Scripts)

	ENVIRONMENT VARIABLES for Symbol Resolution:
	_NT_SYMBOL_PATH : Path(s) to symbol files: *.PDB
	_NT_SYMCACHE_PATH : Path(s) to symcache files: *.SYMCACHE
	See: https://github.com/microsoft/MSO-Scripts/wiki/Symbol-Resolution

	ENVIRONMENT VARIABLES (set by this script):
	$Env:UserProfileAlt = $Env:USERPROFILE of currently logged-on user
	$Env:LocalAppDataAlt = $Env:LOCALAPPDATA of currently logged-on user
	$Env:TempAlt = $Env:TEMP of currently logged-on user

	GLOBAL VARIABLES (set by host script):
	$script:ScriptHomePath: the path of the originally invoked script: Trace*.ps1
	$script:ScriptRootPath: the base path of the script-set and the INCLUDE files
	$script:PSScriptParams: stable copy of PSBoundParameters

	SCRIPT VARIABLES (set by this script)
	$script:WPR_Path: Full path of WPR.exe if discoverable
	$script:WPR_PreWin10: $True if WPR version 10+
	$script:WPR_Win10Ver: Version of WPR if Win10+
#>

function ScriptRootPathString { return $ScriptRootPath.ToString().TrimEnd('\\') }
function ScriptHomePathString { return $ScriptHomePath.ToString().TrimEnd('\\') }
# The pre-Win10 script-set is here:
function ScriptPreWin10Path   { return "$(ScriptRootPathString)\PreWin10" }

# Set by EnsureTracePath (default = $Env:TRACE_PATH or $Env:LOCALAPPDATA\MSO-Scripts)
$script:TracePath = $Null

<# Return false if this script is running in the PreWin10 sub-folder. #>
function IsModernScript { return ((ScriptHomePathString) -ne (ScriptPreWin10Path)) }

$script:WPR_Path = $Null
$script:WPR_PreWin10 = $False
$script:WPR_Win10Ver = $Null
$script:WPR_Flushable = $False

[string[]]$script:Providers = $Null
[bool]$script:fProvidersCollected = $False

<#
	Show the help/usage info.
#>
function WriteUsage
{
	$Command = "$(ScriptHomePathString)\$($script:MyInvocation.MyCommand)"

	Write-Status "Get-Help $Command"
	$Result = (Get-Help $Command) | Out-String
	Write-Help $Result
}


<#
	Return an array containing the arguments.
#>
function GetArgs { return $Args }


<#
	Turn an array into a single, space-separated string of all the elements.
#>
function StringFromArray { Param ([string[]] $StrArgs) return "$StrArgs" } # for PSv2


$script:InfoColor =   "DarkYellow"
$script:WarnColor =   "Magenta"
$script:ErrorColor =  "Red"
$script:ActionColor = "Cyan"
$script:HelpColor =   "White"
$script:DebugColor =  "Blue"

<#
	Write messages to the output / console.
	Write-Host is the most versatile (color, multiple strings, etc.), but not always preferred.
#>
function Write-Msg    { Write-Host @Args }

function Write-Info   { Write-Host -Fore:$InfoColor @Args }

function Write-Warn   { if (!(DoNoWarn)) { Write-Host -Fore:$WarnColor @Args }}

function Write-Err    { Write-Host -Fore:$ErrorColor @Args }

function Write-Action { Write-Host -Fore:$ActionColor @Args }

function Write-Help   { Write-Host -Fore:$HelpColor @Args }

function Write-Dbg    { Write-Host -Fore:$DebugColor @Args }

function Write-Color
{
Param (
	[string]$Color
)
	Write-Host -Fore:$Color @Args
}


<#
	Write status messages as VERBOSE: ...
	Accept multiple strings, which print space-separated.
#>
function Write-Status { Write-Verbose (StringFromArray $Args) }


<#
	For debugging: Write-Vars 'Label' '$foo' '$expr'
	*** Invocations must not appear in released code. ***
#>
function Write-Vars
{
	foreach ($Arg in $Args)
	{
		if ($Arg -notlike '*$*') { Write-Host -Fore:$DebugColor -NoNewLine "$Arg`t" }
		else { Write-Host -Fore:$DebugColor -NoNewLine "$Arg = $(IEX $Arg)`t" }
	}
	Write-Host # NewLine
}


<#
	Verbose mode enabled!
#>
function DoVerbose { return $VerbosePreference -ne 'SilentlyContinue' }


<#
	NoWarn mode enabled!
	Script invoked with: -WarningAction:Silent[lyContinue]
	https://github.com/microsoft/MSO-Scripts/wiki/Troubleshooting#tti
#>
function DoNoWarn { return $WarningPreference -eq 'SilentlyContinue' }


<#
	Pre PS7: $Condition ? $Result1 : $Result2
#>
function Ternary
{
Param (
	[bool]$Condition,
	$Result1,
	$Result2
)
	if ($Condition) { return $Result1 } else { return $Result2 }
}


<#
	The CMD scripts (Trace*.bat) set this environment variable via PowerShell's -EP param.
	Return $True if it's set.
#>
function InvokedFromCMD { return !!($Env:PSExecutionPolicyPreference) }


<#
	Reset $Error and $LastExitCode
	It is not obvious how to do this consistently.
#>
function ResetError
{
	$Error.Clear()
	$global:LastExitCode = 0
}


<#
	Return the true processor architecture: x86, AMD64, etc.
	https://learn.microsoft.com/en-us/archive/blogs/david.wang/howto-detect-process-bitness
	https://learn.microsoft.com/en-us/windows/win32/winprog64/wow64-implementation-details
#>
function GetProcessorArch
{
	$Arch = $Env:PROCESSOR_ARCHITEW6432
	if (!$Arch) { $Arch = $Env:PROCESSOR_ARCHITECTURE }
	return $Arch
}


<#
	Return just the name of this script.
#>
function GetScriptName
{
	return $script:MyInvocation.MyCommand -replace '\.\w+$' # strip the extension (.ps1)
}


<#
	Return the script name with the originally used path if available.
#>
function GetScriptCommand
{
	$Command = $script:MyInvocation.InvocationName

	# Likely invoked from a batch file OR invoked from another PowerShell script: Return just the file name without extension.
	if ((InvokedFromCMD) -or ($Command -like "&*")) { $Command = GetScriptName }

	return $Command
}


<#
	Verify that the function did not unexpectedly return multiple values.
	https://stackoverflow.com/questions/29556437/how-to-return-one-and-only-one-value-from-a-powershell-function
#>
function ValidateResult
{
Param ( [string]$Function,
	$Result
)
	if (!(DoVerbose)) { return }

	if ($Result -eq $Null)
	{
		Write-Status "$Function returned Null"
	}
	elseif ($Result.GetType().FullName -eq "System.Object[]")
	{
		Write-Status "$Function returned:" System.Object[$Result.Count]
		foreach ($val in $Result) { Write-Status "  [$($val.GetType())]" $val }
	}
	elseif ($Result.GetType().FullName -eq "System.String[]")
	{
		Write-Status "$Function returned:" System.String[$Result.Count]
		foreach ($val in $Result) { Write-Status "  [$($val.GetType())]" $val }
	}
}


<#
	Ensure the folder exists and is writable.
	Creates the folder if needed.
#>
function EnsureWritableFolder
{
Param (
	[string]$Path
)
	if (!$Path) { return $False }

	$TestPath = "$Path\Test.tmp"
	if (New-Item -Path $TestPath -ItemType File -Force -ErrorAction:SilentlyContinue)
	{
		Remove-Item -LiteralPath $TestPath -ErrorAction:SilentlyContinue
		return $True
	}
	return $False
}


<#
	Validate the $script:TracePath variable or set its default value:
		$Env:TRACE_PATH
	OR	$Env:LOCALAPPDATA\MSO-Scripts
	This is where new trace files get written, as well as intermediate files.
	Upon return: $Env:TRACE_PATH = $script:TracePath
#>
function EnsureTracePath
{
	SetLoggedOnUserEnv > $Null

	if (EnsureWritableFolder $script:TracePath) { return } # Sanity and $Null check

	if ($Env:TRACE_PATH)
	{
		Write-Status "TRACE_PATH =" $Env:TRACE_PATH
		if (EnsureWritableFolder $Env:TRACE_PATH)
		{
			$script:TracePath = $Env:TRACE_PATH
			return
		}
	}

	# $Env:LocalAppDataAlt\MSO-Scripts
	if ($Env:LocalAppDataAlt)
	{
		$script:TracePath = "$Env:LocalAppDataAlt\MSO-Scripts"
		if (EnsureWritableFolder $script:TracePath)
		{
			$Env:TRACE_PATH = $script:TracePath
			return
		}
	}

	if ($Env:TempAlt)
	{
		# $Env:TEMP\MSO-Scripts
		# Switching between Admin and non-Admin can change the TEMP path.
		# $script:TracePath = $Env:TEMP\MSO-Scripts but not $Env:TEMP\1\MSO-Scripts, etc.

		$script:TracePath = $Env:TempAlt
		if ($script:TracePath -match "Temp\\\d+$")
		{
			$script:TracePath = Resolve-Path "$script:TracePath\.."
		}

		$script:TracePath = "$script:TracePath\MSO-Scripts"
		if (EnsureWritableFolder $script:TracePath)
		{
			$Env:TRACE_PATH = $script:TracePath
			return
		}
	}

	# Last chance: %TEMP% or c:\Windows\Temp
	if ($Env:TEMP)
	{
		$script:TracePath = $Env:TEMP.Trim('"').TrimEnd('\')
	}
	elseif ($Env:SystemRoot)
	{
		$script:TracePath = "$Env:SystemRoot\Temp"
	}

	$script:TracePath = "$script:TracePath\MSO-Scripts"
	if (EnsureWritableFolder $script:TracePath)
	{
		$Env:TRACE_PATH = $script:TracePath
		return
	}

	Write-Err "Could not create a folder for the trace, any of:"
	if (InvokedFromCMD)
	{
		Write-Err "%LocalAppData%\MSO-Scripts, %TEMP%\MSO-Scripts, %SystemRoot%\Temp\MSO-Scripts"
	}
	else
	{
		Write-Err "`%Env:LocalAppData\MSO-Scripts, `%Env:TEMP\MSO-Scripts, `%Env:SystemRoot\Temp\MSO-Scripts"
	}

	exit 1
}


<#
	Write a registry value with the given key path, name and type. (Returns nothing.)
	Can auto-choose the type: [string]->REG_SZ, [int]->REG_DWORD, [long]->REG_QWORD
	Any error will be in: $Error[0]
#>
function SetRegistryValue
{
Param (
	[string]$Path,
	[string]$Name,
	# Can be $Null
	[string]$Type,
	$Value
)
	if (!(Test-Path -Path $Path -ErrorAction:SilentlyContinue))
	{
		New-Item -Path $Path -Force -ErrorAction:SilentlyContinue >$Null
	}
	ResetError
	New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force -ErrorAction:SilentlyContinue >$Null
}


<#
	Return the registry value with the given key path and name, else null.
#>
function GetRegistryValue
{
Param (
	[string]$Path,
	# Return the default value if $Null
	[string]$Name
)
	if (!(Test-Path -Path $Path -ErrorAction:SilentlyContinue)) { return $Null }
	$RegVal = Get-ItemProperty -Path $Path -Name $Name -ErrorAction:SilentlyContinue
	if (!$Name) { $Name = '(default)' }
	if ($RegVal) { return $RegVal.($Name) } # for strict mode
	return $Null
}


<#
	Remove the registry value with the given key path and name.
#>
function ClearRegistryValue
{
Param (
	[string]$Path,
	# Clear the default value if $Null
	[string]$Name
)
	if (!$Name) { $Name = '(default)' }
	Remove-ItemProperty -Path $Path -Name $Name -ErrorAction:SilentlyContinue
}


$script:OSRestartTime = $Null

<#
	Get the local datetime that the OS last restarted as: [int64]
#>
function GetOSRestartTime
{
	if ($script:OSRestartTime) { return $script:OSRestartTime }
	$EndTime = GetRegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Windows' 'ShutdownTime'
	if (!$EndTime) { return $Null }
	$script:OSRestartTime = [bitconverter]::ToInt64($EndTime,0) # assumes little-endian
	return $script:OSRestartTime
}


# Store registry values for ProfileStartTime here:
$script:RegPathStatus = "HKCU:\Software\Microsoft\Office Test\MSO-Scripts"


<#
	TraceCPU + MSO-Trace-CPU#Lean => CPU#Lean
	TraceCPU + MSO-Trace-CPU.Boot => TraceCPU#Boot
	TraceCPU + MSO-Trace-CPU#Lean.Boot => TraceCPU#Lean.Boot
#>
function NameFromScriptInstance
{
Param (
	[string]$InstanceName
)
	$Script = GetScriptName
	if ($InstanceName -like '*[#.]*')
	{
		return "$Script#$(($InstanceName -split '[#.]',2)[-1])"
	}
	return $Script
}


<#
	Record the local filetime that the given profile started recording.
	Any error will be in: Error[0]
#>
function SetProfileStartTime
{
Param (
	[string]$Instance
)
	$Name = NameFromScriptInstance $Instance
	SetRegistryValue $RegPathStatus $Name "QWORD" (Get-Date).ToFileTime()
}


<#
	Set the given profile start time to 0.
	Any error will be in: Error[0]
#>
function ClearProfileStartTime
{
Param (
	[string]$Instance
)
	$Name = NameFromScriptInstance $Instance
	SetRegistryValue $RegPathStatus $Name "QWORD" 0
}


<#
	Return the local datetime that the given profile started recording, else Null.
#>
function GetProfileStartDateTimeByName
{
Param (
	[string]$Name,
	[switch]$XSession # cross-session
)
	if (!$Name) { Write-Dbg "Null instance name!" }

	$StartTime = GetRegistryValue $RegPathStatus $Name
	if ($StartTime -isnot [int64]) { return $Null }
	if (!$StartTime) { return $Null }

	# Sanity checks

	$CurrentTime = (Get-Date).ToFileTime()
	if ($StartTime -gt $CurrentTime) { return $Null }

	if ($XSession)
	{
		return [datetime]::FromFileTime($StartTime)
	}

	# If there was an intervening OS restart then the recording was reset.

	$RestartTime = GetOSRestartTime
	if ($RestartTime)
	{
		if ($StartTime -le $RestartTime) { return $Null }
	}
	return [datetime]::FromFileTime($StartTime)
}

<#
	Return the local datetime that the given profile started recording, else Null.
#>
function GetProfileStartDateTime
{
Param (
	[string]$Instance,
	[switch]$XSession # cross-session
)
	if (!($Instance -like '*-*')) { Write-Dbg "Unexpected instance name! $Instance" }

	$Name = NameFromScriptInstance $Instance

	return GetProfileStartDateTimeByName $Name -XSession:$XSession
}


<#
	List all running profiles with their start time.
	If $CurrentScript then list only those profiles started with the current script (including -Lean, -Snap, etc.)
	Return $True if any profiles were listed.
#>
function TestRunningProfiles
{
Param (
	[switch]$CurrentScript
)
	if (!(Test-Path -Path $RegPathStatus -ErrorAction:SilentlyContinue)) { return $False }

	# Get a list of registry names
	[string[]] $Names = Get-Item -Path $RegPathStatus -ErrorAction:SilentlyContinue | Select-Object -ExpandProperty Property
	if (!$Names) { return $False } # PSv2

	$ScriptName = GetScriptName
	$fStarted = $False
	foreach ($Name in $Names)
	{
		# $Name = <ScriptName> or <ScriptName>#<Switch>

		$IsBoot = ($Name -like '*[#.]Boot')

		$StartTime = GetProfileStartDateTimeByName $Name -XSession:$IsBoot

		if ($StartTime)
		{
			if (!$CurrentScript -or ($Name -eq $ScriptName) -or ($Name -like "$ScriptName#*"))
			{
				if (!$fStarted) { Write-Msg; $fStarted = $True }
				$Parts = $Name -split '#'
				$Name = Ternary ($Parts.length -eq 1) "$Name Start" "$($Parts[0]) Start -$($Parts[-1])"

				if (!$IsBoot)
				{ Write-Warn "`"$Name`" began tracing at $StartTime" }
				else
				{ Write-Warn "The ETW AutoLogger was configured with `"$Name`" at $StartTime" }
			}
		}
	}
	if ($fStarted) { Write-Msg }

	return $fStarted
}


function ListRunningProfiles
{
Param (
	[switch]$CurrentScript
)
	$Null = TestRunningProfiles -CurrentScript:$CurrentScript
}


<#
	Return a string array of providers for the given running trace.
#>
function GetRunningTraceProviders
{
Param (
	[string]$InstanceName
)
	if ($script:fProvidersCollected) { return $script:Providers }

	$script:fProvidersCollected = $True

	$Result = (InvokeWPR -Status collectors -InstanceName $InstanceName) -split "`r`n"
	if (!$Result) { return $Null }
	if (($Result.Count -lt 10) -and $Result.Contains("WPR is not recording")) { return $Null }

	<# Format:
		...
		Collector Name ...
		...
		Providers
		System Keywords
			<SYSTEM PROVIDERS>
		System Stacks
			...
		Collector Name ...
		...
		Providers
			<PROVIDERS>
	#>

	[string[]] $ProvidersT = $Null
	$Keep = $False
	foreach ($Line in $Result)
	{
		if ($Line -eq "System Keywords") { continue }
		if ($Line -eq "Providers")
		{ $Keep = $True; }
		elseif ($Keep -and ($Line -like "`t*"))
		{ if ($Line -notlike "`t`t*") { $ProvidersT += $Line.Trim() } }
		else
		{ $Keep = $False }
	}
	$script:Providers = $ProvidersT

	return $script:Providers
}


<#
	Determine if this tracing session is collecting CLR info.
#>
function IsCLRTrace
{
Param (
	[string]$InstanceName
)
	$Result = GetRunningTraceProviders $InstanceName

#	if (!$Result) { return $True } # default

	return ($Result -like "*DotNETRuntime*") -or ($Result -like "*e13c0d23*")
}


<#
	Get a list of all currently running ETW collectors/loggers.
	Can return $Null
#>
function GetRunningCollectorSets
{
	$Collectors = $Null
	$TempFile = Join-Path -Path $env:Temp -ChildPath 'CollectorSet.tmp' # Just get a file name

	# TypePerf gives a more accurate count then LogMan (because of security).
	try
	{
		Start-Process -FilePath "TypePerf.exe" -ArgumentList '-qx "Event Tracing for Windows Session"' -NoNewWindow -Wait -RedirectStandardOutput $TempFile -ErrorAction:Stop
		$Collectors = Get-Content -LiteralPath $TempFile | Select-String -SimpleMatch -Pattern 'Events Lost' -CaseSensitive
		# Format: "\Event Tracing for Windows Session(DefenderApiLogger)\Events Lost"
		$Collectors = $Collectors -replace '\\Event Tracing for Windows Session\((?<Logger>.+)\)\\Events Lost$','${Logger}'
	}
	catch
	{
		$Collectors = $Null
	}

	if (!$Collectors)
	{
		try
		{
			Start-Process -FilePath "LogMan.exe" -ArgumentList 'Query -ets -n *' -NoNewWindow -Wait -RedirectStandardOutput $TempFile -ErrorAction:Stop
			$Collectors = Get-Content -LiteralPath $TempFile | Select-String -SimpleMatch -Pattern 'Running' -CaseSensitive
			$Collectors = $Collectors -replace '\s+Trace\s+.+' # Name//___Trace___Running`n
		}
		catch
		{
			$Collectors = $Null
		}
	}

	Remove-Item $TempFile
	return $Collectors
}


<#
	Handle cryptic WPR error 0x800705aa: E_NOSYSTEMRESOURCES
	"Insufficient system resources."

	Note that we try to minimize this effect
	by giving all WPRP SystemCollector/EventCollector items the same name.
	Thus WPR coalesces the collectors and reduces the chance of bumping up against this limit.
#>
function HandleInsufficientResources
{
	Write-Err "Insufficient system resources!"
	$Tracers = GetRunningCollectorSets

	Write-Err "Usually this means that too many trace collectors are currently running."
	Write-Err "See `"ERROR_NO_SYSTEM_RESOURCES`" here:"
	Write-Err "https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew#return-value"
	Write-Err "https://devblogs.microsoft.com/performance-diagnostics/wpr-fails-to-start-insufficient-system-resources"

	if (!$Tracers) { $Tracers = GetRunningCollectorSets } # Try again!?

	# Technically the internal trace count limit is 64, but practically it's closer to 44 by a LogMan count.
	if ($Tracers -and ($Tracers.Count -ge 40)) # empirical
	{
		Write-Warn "There are currently $($Tracers.Count) running trace collectors."
		Write-Warn "To see the list, run (with Admin access):"
		Write-Warn "`tLogMan -ets Query"
		Write-Warn "To stop a trace collector, run:"
		Write-Warn "`tLogMan -ets Stop `"`<Data Collector Set Name`>`""
	}
}


<#
	Handle cryptic error 0x80010106: RPC_E_CHANGED_MODE
	"Cannot change thread mode after it is set."

	This is known to happen when generating NGEN/embedded PDBs for managed modules.
	Restarting Windows usually resolves the problem.
	More recent versions of WPR have additional safeguards to reduce its occurrence.
#>
function HandleChangeThreadMode
{
	Write-Err "Windows Performance Recorder (WPR): Internal COM Error"
	Write-Warn "This issue usually resolves by restarting Windows."
	Write-Warn "If this occurs frequently, make sure that you have a very recent version of WPR:`n"
	WriteWPTInstallMessage "WPR.exe"
}


<#
	Handle error 0xc5583014: Event Collector In Use
	We can get a list of the currently running ETW Providers/Collecors.
	After that it may not be clear what we can do here.
#>
function HandleCollectorInUse
{
	$Collectors = GetRunningCollectorSets
	$Collector_WPR = $Collectors -like "WPR_initiated*"
	$Collector_SYS = $Collectors -cmatch "PROC.+\sTRACE|NT\sKernel\sLogger"

	if ($Collector_WPR)
	{
		Write-Err "Try canceling the currently running WPR traces, or run:"
		foreach ($Name in $Collector_WPR)
		{
			Write-Err "`tLogMan -ets Stop `"$Name`""
		}
	}
	elseif ($Collector_SYS)
	{
		Write-Err "Please exit other system-tracing processes such as Process Explorer, Process Monitor, etc."
	}
	elseif (DoVerbose)
	{
		Write-Err "Here is a list of all of the currently running data collectors:"
		foreach ($Name in $Collectors) { Write-Err "`t$Name" }
		Write-Err
		Write-Err "It is normal for there to be dozens of these collectors running concurrently."
		Write-Err "It is not clear which of these may be interfering with starting a new trace."
		Write-Err "The best solution may be to restart Windows."
		Write-Err "Or individual collectors can be stopped with this command:"
		Write-err "`tLogMan -ets Stop `"Collector Name`""
	}
	else
	{
		Write-Err "For more info, please re-run the Trace command with: -verbose"
	}

}


<#
	Not all versions of WPR are consistent.
	Some give error 0xc5580612 on some OS's with some WPRP configurations.
#>
function HandleWPRCompatibility
{
Param (
	$WprParams
)
	Write-Err (GetCmdVerbose $script:WPR_Path $WprParams)

	Write-Warn "`nThis suggests that there is a problem with the WPR Recording Profile (.wprp file)."
	Write-Warn "See: https://devblogs.microsoft.com/performance-diagnostics/authoring-custom-profile-part3/#:~:text=Common%20Failure%20Codes"
	Write-Warn "But it could also be due to a version of WPR.exe which is incompatible with the current OS."

	$WinDir32 = "$($Env:SystemRoot)\System32"
	$WPRPath32 = "$WinDir32\WPR.exe"

	if (($script:WPR_Path -ne $WPRPath32) -and (Test-Path -PathType leaf -Path $WPRPath32 -ErrorAction:SilentlyContinue))
	{
		Write-Warn "Try setting the WPT_PATH environment variable to: $WinDir32"
	}
	else
	{
		Write-Warn "Try setting the WPT_PATH environment variable to the folder of a different version of WPR.exe"
	}
}


<#
	0x800700b7 "Cannot create a file when that file already exists."
	This happens when a WPRP file is referenced twice.
#>
function HandleFileConflict
{
Param (
	$WprParams
)
	Write-Err (GetCmdVerbose $script:WPR_Path $WprParams)

	Write-Err "`nWPR returned error 0x800700b7: Cannot create a file when that file already exists."

	Write-Warn "`nThis suggests that a WPR Recording Profile (.wprp file) is referenced twice."

	# Report any WPRP files which may be referenced twice.

	$fProfile = $False
	[string[]]$paths = $Null

	foreach ($WprParam in $WprParams)
	{
		if ($fProfile)
		{
			$path = ReplaceEnv (StripProfileName $WprParam) # Just the .wprp path\file, not the profile name.
			if ($paths -contains $path)
			{
				Write-Warn "Duplicated: $path"
			}
			else
			{
				$paths += $path
			}
		}
		$fProfile = ($WprParam -eq '-Start')
	}

	if (!(DoVerbose)) { Write-Warn "Perhaps rerun the command with: -verbose" }
}


<#
	0x80070008: Not enough memory resources are available to process this command.
#>
function HandleOOM
{
Param (
	$WprParams
)
	Write-Err (GetCmdVerbose $script:WPR_Path $WprParams)

	Write-Err "`nWPR returned error 0x80070008: Not enough memory resources are available to process this command."

	Write-Warn "This usually resolves by restarting Windows."
	if ($WprParams -like '*-filemode*')
	{
		Write-Warn "Or simply try again."
	}
	else
	{
		Write-Warn "Or simply try again without: -Loop"
	}
	Write-Warn "Windows may also be low on Virtual Memory."
	Write-Warn "See: https://www.windowscentral.com/how-change-virtual-memory-size-windows-10"
	Write-Warn "And: https://www.windowscentral.com/software-apps/windows-11/how-to-manage-virtual-memory-on-windows-11"
}


<#
	Use WMI (deprecated after PSv2) to return the SID of the logged-on user for the current Windows Session.
	Return: $Null if failure; $False if it's unchanged from the current context; [string]SID if success.
#>
function GetLoggedOnUserSID_WMI
{
Param (
	$UserName
)
	try
	{
		if (!$UserName)
		{
			$UserName = (Get-WMIObject -Class Win32_ComputerSystem | select username).username
			Write-Status "WMI: Logged on user name = $UserName"
		}

		if (!$UserName)
		{
			Write-Status "WMI: Failed to get the logged on user name."
			return $Null
		}

		$CurrentUserName = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
		if ($UserName -eq $CurrentUserName) { return $False }

		if ($Env:UserDNSDomain)
		{
			# Domain account is too slow!
			Write-Status "WMI: Aborting Win32_UserAccount operation on domain $Env:UserDNSDomain" 
			return $Null
		}

		$UserAcct = $Null
		$NameSplit = $UserName -split '\\'

		if ($NameSplit.count -eq 2)
		{
			$UserAcct = [wmi]"win32_userAccount.Domain='$($NameSplit[0])',Name='$($NameSplit[1])'"
		}

		if (!$UserAcct)
		{
			Write-Status "WMI: Failed to get the logged on user account for $UserName"
			return $Null
		}

		Write-Status "WMI: Logged on user SID for $UserName = $($UserAcct.sid)"
		return $UserAcct.sid
	}
	catch
	{
		Write-Status "WMI: Failed to get the logged on user SID!"
		return $Null
	}
}


<#
	Use CIM (PSv3+) to return the SID of the logged-on user for the current Windows Session.
	Return: $Null if failure; $False if it's unchanged from the current context; [string]SID if success.
#>
function GetLoggedOnUserSID_CIM
{
Param (
	$UserName
)
	try
	{
		if (!$UserName)
		{
			$System = Get-CimInstance -ClassName Win32_ComputerSystem -Property "UserName" -KeyOnly -ErrorAction:Stop # throw
		#	$System = Get-CimInstance -Query "select UserName from Win32_ComputerSystem" -ErrorAction:Stop # throw

			if ($System)
			{
				$UserName = $System.UserName
				Write-Status "CIM: Logged on user name = $UserName"
			}
		}

		if (!$UserName)
		{
			Write-Status "CIM: Failed to get the logged on user name."
			return $Null
		}

		$CurrentUserName = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
		if ($UserName -eq $CurrentUserName) { return $False }

		$UserNameX = $UserName -replace '\\','\\' # replace \ with \\

		$UserAcct = Get-CimInstance -ClassName Win32_UserAccount -Property "SID","Caption" -KeyOnly -Filter "Caption='$UserNameX'" -ErrorAction:Stop # throw
	#	$UserAcct = Get-CimInstance -Query "select SID from Win32_UserAccount where Caption='$UserNameX'" -ErrorAction:Stop # throw

		if (!$UserAcct)
		{
			Write-Status "CIM: Failed to get the logged on user account for $UserName"
			return $Null
		}

		Write-Status "CIM: Logged on user SID for $UserName = $($UserAcct.sid)"
		return $UserAcct.sid
	}
	catch
	{
		Write-Status "CIM: Failed to get the logged on user SID!"
		return $Null
	}
}


<#
	Use the Registry (else CIM/WMI) to return the SID of the logged-on user for the current Windows Session.
	Return: $Null if failure; $False if it's unchanged from the current context; [string]SID if success.
#>
function GetLoggedOnUserSID
{
	$LoggedOnUserName = $Null

	$SessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId

	if ($SessionId)
	{
		$KeySessionData = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\SessionData\$SessionId"

		# Get the Domain\Name of the currently logged-on user.

		$LoggedOnUserName = GetRegistryValue $KeySessionData "LoggedOnUser"
		if (!$LoggedOnUserName) { $LoggedOnUserName = GetRegistryValue $KeySessionData "LoggedOnSAMUser" }

		if ($LoggedOnUserName)
		{
			# Get and test the Domain\Name of the user context of this instance of PowerShell.

			$CurrentUserName = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
			if ($LoggedOnUserName -eq $CurrentUserName) { return $False } # Same Account
		}

		# Get the SID of the currently logged-on user.

		$LoggedOnSID = GetRegistryValue $KeySessionData "LoggedOnUserSID"

		if ($LoggedOnSID)
		{
			Write-Status "Registry: Logged on user SID for $LoggedOnUserName = $LoggedOnSID"
			return $LoggedOnSID
		}
	}

	if ($Host.Version.Major -ge 3)
	{
		return GetLoggedOnUserSID_CIM $LoggedOnUserName
	}
	else
	{
		return GetLoggedOnUserSID_WMI $LoggedOnUserName
	}
}


<#
	If this is an "elevated" CMD/PowerShell window, it might run within the context of a different (Admin) user.
	If so, set these environment variables to be those of the logged-in, Standard User environment:
		UserProfileAlt, LocalAppDataAlt, TempAlt
	Return: $Null if nothing changed; $False on failure; else $True
#>
function SetLoggedOnUserEnv
{
	if ($Env:TempAlt) { return $True }

	$Env:UserProfileAlt = $Env:USERPROFILE.Trim('"').TrimEnd('\')
	$Env:LocalAppDataAlt = $Env:LOCALAPPDATA.Trim('"').TrimEnd('\')
	$Env:TempAlt = $Env:TEMP.Trim('"').TrimEnd('\')

	# Get the logged-on User SID.

	$LoggedOnSID = GetLoggedOnUserSID

	if (!$LoggedOnSID) { return $LoggedOnSID } # $Null or $False

	# Get, test, and update the env-var: USERPROFILE

	$VolatileEnv = "Registry::HKEY_USERS\$LoggedOnSID\Volatile Environment"
	$LoggedOnUserProfile = GetRegistryValue $VolatileEnv "USERPROFILE"
	if (!$LoggedOnUserProfile) { $LoggedOnUserProfile = GetRegistryValue "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$LoggedOnSID" "ProfileImagePath" }
	if (!$LoggedOnUserProfile) { return $False }
	if ($LoggedOnUserProfile -eq $Env:USERPROFILE) { return $Null }

	# Set USERPROFILE now, because other env-vars depend on: %USERPROFILE%\..
	# Change it back later.

	$Env:USERPROFILE = $LoggedOnUserProfile

	# Get and update the env-var: LOCALAPPDATA

	$LoggedOnAppData = GetRegistryValue $VolatileEnv "LOCALAPPDATA"
	if (!$LoggedOnAppData) { $LoggedOnAppData = "$LoggedOnUserProfile\AppData\Local" }

	# Set LOCALAPPDATA now, because other env-vars could depend on: %LOCALAPPDATA%\..
	# Change it back later.

	$Env:LOCALAPPDATA = $LoggedOnAppData

	$SessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId

	# Get and update the env-var: TEMP
	# In some cases this can be a "Volatile Environment" variable of the form: ..\AppData\Local\Temp\2
	# https://devblogs.microsoft.com/oldnewthing/20110125-00/?p=11673

	# Automatically Expanded: REG_MULTI_SZ %USERPROFILE%\AppData\Local\Temp[\N]
	$LoggedOnTemp = GetRegistryValue "Registry::HKEY_USERS\$LoggedOnSID\Volatile Environment\$SessionId" "TEMP"
	if ($LoggedOnTemp -and (!(Test-Path -PathType container -LiteralPath $LoggedOnTemp -ErrorAction:SilentlyContinue))) { $LoggedOnTemp = $Null } # sanity check
	if (!$LoggedOnTemp) { $LoggedOnTemp = GetRegistryValue "Registry::HKEY_USERS\$LoggedOnSID\Environment" "TEMP" }
	if (!$LoggedOnTemp) { $LoggedOnTemp = "$LoggedOnAppData\Temp" }

	$Env:USERPROFILE = $Env:UserProfileAlt # Original
	$Env:LOCALAPPDATA = $Env:LocalAppDataAlt # Original

	if (!(EnsureWritableFolder $LoggedOnTemp)) { return $False }

	if (InvokedFromCMD)
	{
		Write-Status "Setting %UserProfileAlt% = $LoggedOnUserProfile"
		Write-Status "Setting %LocalAppDataAlt% = $LoggedOnAppData"
		Write-Status "Setting %TempAlt% = $LoggedOnTemp"
	}
	else
	{
		Write-Status "Setting `$Env:UserProfileAlt = $LoggedOnUserProfile"
		Write-Status "Setting `$Env:LocalAppDataAlt = $LoggedOnAppData"
		Write-Status "Setting `$Env:TempAlt = $LoggedOnTemp"
	}

	$Env:UserProfileAlt = $LoggedOnUserProfile
	$Env:LocalAppDataAlt = $LoggedOnAppData
	$Env:TempAlt = $LoggedOnTemp
}


[string]$script:WinevtPublishers = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\Publishers'

<#
	Test ETW Provider Registration
#>
function TestProviderRegistration
{
Param (
	[guid]$ProviderId
)
	$ProviderPath = "$script:WinevtPublishers\{$ProviderId}"
	$ResFilePath = GetRegistryValue $ProviderPath "ResourceFileName"
	if ([string]::IsNullOrEmpty($ResFilePath)) { return $False }
	return (Test-Path -PathType leaf -Path $ResFilePath -ErrorAction:SilentlyContinue)
}


<#
	Given a GUID string or the name of a registered, manifested ETW provider,
	return the corresponding registry key.
#>
function GetETWManifestRegistryKey
{
Param (
	[string]$GuidOrName
)
	[Microsoft.Win32.RegistryKey]$key = $Null
	[guid]$guid = [guid]::Empty

	# Is $GuidOrName a GUID?
	if (![guid]::TryParse($GuidOrName, [ref]$guid))
	{
		# $GuidOrName is not a GUID.
		# Expensive: Find the ETW provider's subkey(s) whose default value is the given name.
		[array]$rgkey = Get-ChildItem $script:WinevtPublishers | Where-Object { $_.GetValue($Null) -eq $GuidOrName }
		if (!$rgkey) { return $Null }
		$key = $rgKey[0]
	}
	else
	{
		# $GuidOrName is a GUID.
		$key = Get-Item "$script:WinevtPublishers\{$guid}" -ErrorAction:SilentlyContinue
	}

	return $key
}



[string[]]$script:ManifestPathsLoaded = $Null # List of paths of manifests already registered.

<#
	Ensure that the ETW providers for the given manifest are registered.
	Returns nothing.

	".\*" is relative to $ScriptHomePath
	"..\*" is relative to $ScriptRootPath
#>
function EnsureETWProvider
{
Param (
	# Full path or "..\OETW\MsoEtw??.man"
	[string]$ManifestPath
)
	if (!$ManifestPath) { return } # PSv2

	$ManifestPath = MakeFullPath $ManifestPath

	if ($script:ManifestPathsLoaded -contains $ManifestPath) { return }
	$script:ManifestPathsLoaded += $ManifestPath

	try
	{
		$XML = [xml](Get-Content $ManifestPath -ErrorAction:Stop)
	}
	catch
	{
		Write-Status "Failure loading: $ManifestPath"
		return
	}

	$ManifestName = Split-Path -Leaf -Path $ManifestPath
	# The resource files are under the manifest's folder.
	$ResPath = Split-Path -Parent -Path $ManifestPath

	$ResFile = $Null
	$MsgFile = $Null
	$PrmFile = $Null
	[array]$Names = $Null

	$Providers = $XML.instrumentationManifest.instrumentation.events.provider
	foreach ($Provider in $Providers)
	{
		if (!$Provider) { break } # PSv2

		if (!(Get-Member -InputObject $Provider -Name "guid" -MemberType Properties))
		{
			Write-Status "ETW Provider missing GUID in: $ManifestName"
			continue
		}

		if (!(Get-Member -InputObject $Provider -Name "name" -MemberType Properties))
		{
			Write-Status "ETW Provider missing Name in $ManifestName for" $Provider.guid
			continue
		}

		if (TestProviderRegistration($Provider.guid))
		{
			Write-Status "ETW Provider is Registered:" $Provider.name "-" $Provider.guid
			continue
		}

		Write-Status "ETW Provider Not Registered:" $Provider.name "-" $Provider.guid

		$Names += $Provider.name

		# Validate the resource file, message file (opt), parameters file (opt).

		if (Get-Member -InputObject $Provider -Name "resourceFileName" -MemberType Properties)
		{
			$FileT = $Provider.resourceFileName
			if (Test-Path -PathType leaf -Path "$ResPath\$FileT" -ErrorAction:SilentlyContinue)
			{
				if ($ResFile -and ($ResFile -ne $FileT)) { Write-Status "Inconsistent resourceFileNames in $ManifestName" }
				$ResFile = $FileT
			}
			else
			{
				Write-Status "Nonexistent resourceFileName in $ManifestName at: $ResPath\$FileT"
			}
		}
		if (Get-Member -InputObject $Provider -Name "messageFileName" -MemberType Properties)
		{
			$FileT = $Provider.messageFileName
			if (Test-Path -PathType leaf -Path "$ResPath\$FileT" -ErrorAction:SilentlyContinue)
			{
				if ($MsgFile -and ($MsgFile -ne $FileT)) { Write-Status "Inconsistent messageFileNames in: $ManifestName" }
				$MsgFile = $FileT
			}
			else
			{
				Write-Status "Nonexistent messageFileName in $ManifestName at: $ResPath\$FileT"
			}
		}
		if (Get-Member -InputObject $Provider -Name "parameterFileName" -MemberType Properties)
		{
			$FileT = $Provider.parameterFileName
			if (Test-Path -PathType leaf -Path "$ResPath\$FileT" -ErrorAction:SilentlyContinue)
			{
				if ($PrmFile -and ($PrmFile -ne $FileT)) { Write-Status "Inconsistent parameterFileNames in: $ManifestName" }
				$PrmFile = $FileT
			}
			else
			{
				Write-Status "Nonexistent parameterFileName in $ManifestName at: $ResPath\$FileT"
			}
		}
	} # foreach $Provider

	if ($ResFile)
	{
		# ETW manifest resource files need to be on a local, fixed drive.

		if (!(IsAbsFixedDrivePath($ResPath)))
		{
			$ResPathDest = "$Env:ProgramData\ETW_RES"
			md $ResPathDest -ErrorAction:SilentlyContinue >$Null
			try
			{
				Copy-Item -Path "$ResPath\$ResFile" -Destination "$ResPathDest\$ResFile" -ErrorAction:SilentlyContinue
				if ($MsgFile -and ($MsgFile -ne $ResFile)) { Copy-Item -Path "$ResPath\$MsgFile" -Destination "$ResPathDest\$ResFile" -ErrorAction:SilentlyContinue >$Null }
				if ($PrmFile -and ($PrmFile -ne $ResFile)) { Copy-Item -Path "$ResPath\$PrmFile" -Destination "$ResPathDest\$ResFile" -ErrorAction:SilentlyContinue >$Null }
				Copy-Item -Path $ManifestPath -Destination "$ResPathDest\$ManifestName" -ErrorAction:SilentlyContinue
				$ResPath = $ResPathDest
			}
			catch
			{
				# Leave the files where they are.
				Write-Status "Failed to copy ETW Manifest Resource files to: $ResPathDest"
			}
		}

		# Register the ETW provider using: wevtutil.exe im Manifest.man /rf:Resource.res ...

		$global:LastExitCode = 2 # Not Found

		# Double-check that the resource file ended up in the right place.
		if (Test-Path -PathType leaf -Path "$ResPath\$ResFile" -ErrorAction:SilentlyContinue)
		{
			[array]$cmd = GetArgs im $ManifestPath /rf:$ResPath\$ResFile
			if ($MsgFile) { $cmd += GetArgs /mf:$ResPath\$MsgFile }
			if ($PrmFile) { $cmd += GetArgs /pf:$ResPath\$PrmFile }

			ResetError

			Write-Msg "Registering ETW Manifest:" $Names # all provider names
			WriteCmdVerbose wevtutil.exe $cmd
			& wevtutil.exe $cmd >$Null 2>$Null
		}

		if ($LastExitCode -ne 0) { Write-Status "ETW Manifest Registration failed for $ManifestName. Error: $LastExitCode" }
	}
	elseif ($MsgFile -or $PrmFile)
	{
		Write-Status "Missing resourceFileName in $ManifestName"
	}
} # EnsureETWProvider


<#
	If the parameter is a path beginning with the expansion of a common environment variable
	then replace the expansion with the variable name.
	Quote the parameter if needed.
#>
function ReplaceOneEnv
{
Param (
	[ref]$Param,
	[string]$EnvName
)
	$EnvVal = $ExecutionContext.InvokeCommand.ExpandString("`${Env:$EnvName}")
	if ([string]::IsNullOrEmpty($EnvVal)) { return $False }

	if ($Param.Value -notlike "*$EnvVal\*") { return $False }

	if (InvokedFromCMD)
	{
		$EnvName = "%$EnvName%"
	}
	else
	{
		if ($EnvName -like "*)") # eg. ProgramFiles(x86)
		{
			$EnvName = "`${Env:$EnvName}"
		}
		else
		{
			$EnvName = "`$Env:$EnvName"
		}
	}

	$Param.Value = $Param.Value.Replace($EnvVal, $EnvName)
	if ($Param.Value -notlike '"*') { $Param.Value = "`"$($Param.Value)`"" }

	return $True
} # ReplaceOneEnv


<#
	If the parameter looks like a path then replace certain path environment variables:
	"c:\program files\..." => "%ProgramFiles%\..." or "$Env:ProgramFiles\..."
#>
function ReplaceEnv
{
Param (
	[string]$Param
)
	# If it's a path then replace common environment variables.
	if ($Param -like '*\*')
	{
		# Order: longest to shortest
		if (ReplaceOneEnv ([ref]$Param) 'TEMP') { return $Param }
		if (ReplaceOneEnv ([ref]$Param) 'LocalAppData') { return $Param }
		if (ReplaceOneEnv ([ref]$Param) 'UserProfile') { return $Param }
		if (ReplaceOneEnv ([ref]$Param) 'ProgramFiles(x86)') { return $Param }
		if (ReplaceOneEnv ([ref]$Param) 'ProgramFiles') { return $Param }
		if (ReplaceOneEnv ([ref]$Param) 'SystemRoot') { return $Param }
		if (ReplaceOneEnv ([ref]$Param) 'WPT_Path') { return $Param }
		if ($Param -notlike '"*') { return "`"$Param`"" }
	}
	return $Param
}


<#
	If the path can be represented using an environment variable, return that.
	Else return $Null.
#>
function GetEnvPath
{
Param (
	# Unquoted path
	[string]$Path
)
	$PathQuote = "`"$Path`""
	$PathNew = ReplaceEnv $PathQuote
	if ($PathNew -eq $PathQuote) { return $Null }
	return $PathNew
}


<#
	Create a copy-paste-able / cmd-run-able string representing an executable command.
#>
function GetCmdVerbose
{
Param (
	[string]$Cmd,
	[string[]]$Params
)
	if (!$Params) { return $Cmd }

	[array]$Out = ReplaceEnv $Cmd

	foreach ($Param in $Params)
	{
		$Out += ReplaceEnv $Param
	}

	return $Out
}


<#
	Write a copy-paste-able / cmd-run-able string representing a command to be executed.
#>
function WriteCmdVerbose
{
Param (
	[string]$Cmd,
	[string[]]$Params
)
	# If not -verbose then don't do all this work.
	if (!(DoVerbose)) { return }

	$Out = GetCmdVerbose $Cmd $Params

	if (InvokedFromCMD)
	{
		Write-Status "Executing Command:`n$Out"
	}
	else
	{
		Write-Status "Executing PowerShell Command:`n& $Out"
	}
}


<#
	Get the most recent process with the given name, and with: StartTime >= $PreStartTime
	Return the Process object.
#>
function GetRunningProcess
{
Param (
	[string]$Name,
	[DateTime]$PreStartTime # optional
)
	for ($i = 0; $i -lt 10; $i++)
	{
		Start-Sleep 1

		# Get the version info for the most recently created process with the given name.

		$Processes = Get-Process -name $Name -ErrorAction:SilentlyContinue | Sort-Object -Descending -Property StartTime -ErrorAction:SilentlyContinue

		if ($Processes)
		{
			if ($Processes -is [system.array]) { $Process = $Processes[0] } else { $Process = $Processes } # for PSv2

			if ($PreStartTime -and ($Process.StartTime -lt $PreStartTime)) { continue }

			if (DoVerbose)
			{
				Write-Status "Running:" $Process.ProcessName "v$($Process.MainModule.FileVersionInfo.FileVersion)" "PID =" $Process.id "Running since:" $Process.StartTime
				Write-Status "Path =" (ReplaceEnv $Process.MainModule.FileName)
			}
			return $Process
		}
	}

	return $Null
}


<#
	Ensure that all \paths\ are `"quoted`"
#>
function QuotePath
{
Param(
	[string]$Param
)
	if (($Param -like '*\*') -and ($Param -notlike '"*"')) { return "`"$Param`"" } else { return $Param }
}


<#
	Invoke a short-lived process, such as WPR.
	Returns the error text or the output.
	$global:LastExitCode =
		  1 (ERROR_INVALID_FUNCTION): Failed to run.
		 -1 Bad Parameters (usually).
		258 (WAIT_TIMEOUT): The invoked process ran but timed out (not short-lived).
		329 (ERROR_OPERATION_IN_PROGRESS): Ran a batch file process wrapper.
#>
function InvokeExe
{
Param (
	[string]$ExePath
)
	# Quote all paths.
	[string[]]$Params = $Args | ForEach-Object {QuotePath $_}

	# $Args should be an array rather than an aggregated string, else paths didn't get quoted, etc.
	if (($Params.Count -eq 1) -and ($Params[0] -like "* *")) { Write-Dbg "Invoking $([System.IO.Path]::GetFileName($ExePath)) with aggregated parameters:" $Params[0] }

	WriteCmdVerbose $ExePath $Params
	ResetError

	$psi = New-Object System.Diagnostics.ProcessStartInfo
	$psi.FileName = $ExePath
	# $psi.ArgumentList = $Args # Not available until .NETv5
	$psi.Arguments = $Params
	$psi.RedirectStandardError = $true
	$psi.RedirectStandardOutput = $true
	$psi.UseShellExecute = $false
	$psi.CreateNoWindow = $true
	$psi.ErrorDialog = $false

	$proc = New-Object System.Diagnostics.Process
	$proc.StartInfo = $psi

	try { $proc.Start() > $Null }
	catch { $global:LastExitCode = 1; $proc.Close(); return "Failed to run: $ExePath" }

	if ($proc.Name -eq 'cmd')
	{
		# Running a batch file process wrapper, so we won't be able to capture the output.
		$proc.Close()
		$global:LastExitCode = 329 # ERROR_OPERATION_IN_PROGRESS
		return $Null
	}

	try { $proc.PriorityClass = 'High' } # Short-lived process needs to act quickly.
	catch { <# Already exited? #> }

	$stdout = $proc.StandardOutput.ReadToEnd()
	$stderr = $proc.StandardError.ReadToEnd()

	$ExitCode = 258 # WAIT_TIMEOUT
	if ($proc.WaitForExit(20000)) { $ExitCode = $proc.ExitCode }

	$proc.Close()

	$global:LastExitCode = $ExitCode
	if ($ExitCode -ne 0) { return $stderr }
	return $stdout # "Heap tracing was successfully enabled..."
}


<#
	Write out the WPR params (verbose).
	Invoke the Windows Performance Recorder (WPR.exe).
	Returns the error or the output. Sets: $LastExitCode
#>
function InvokeWPR
{
	if (!$script:WPR_Path) { Write-Dbg "Prerequisite failure: `$script:WPR_Path = `$Null Params = $Args"; return $Null }

	$Return = InvokeExe $script:WPR_Path @Args

	if ($global:LastExitCode -eq 329) # ERROR_OPERATION_IN_PROGRESS
	{
		Write-Warn "Wrapper script for WPR.exe may not work well: $script:WPR_Path"

		if (InvokedFromCMD) { Write-Warn 'Please set WPT_PATH=<path of WPR.exe>' }
		else { Write-Warn 'Please set $Env:WPT_PATH = "<path of WPR.exe>"' }

		$global:LastExitCode = 0
	}

	return $Return
}


<#
	Determine whether this script has Administrator privileges.
#>
function CheckAdminPrivilege
{
	$Principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
	$Administrator = [Security.Principal.WindowsBuiltInRole]::Administrator
	if (!$Principal -or !$Administrator) { return $False }
	return $Principal.IsInRole($Administrator)
}


<#
	Write-Warn instructions on how to download or identify the Windows Performance Toolkit (WPT).
	The parameter should be "wpr.exe" or "wpa.exe" etc.
	$Recent: Only recommend a recent version of WPA.
#>
function WriteWPTInstallMessage
{
Param (
	[string]$Exe,
	[switch]$Recent
)
	$Environ = [Environment]::OSVersion.Version
	$Version = [int]$Environ.Major + [int]$Environ.Minor
<#	$Version:
	 6.1 ->  (7) -> Win7   / Server 2008 R2
	 6.2 ->  (8) -> Win8.0 / Server 2012
	 6.3 ->  (9) -> Win8.1 / Server 2012 R2
	10.0 -> (10) -> Win10  / Server 2016+
	11.0 -> (10*)-> Win11  / Server 2022+  v10.0.22000+
#>
	$Message = $Null

	# Recent versions of WPA (.NET Core) work on Win8.1+

	if (($Exe -eq "wpa.exe") -and ($Version -ge 9))
	{
		$Message += "`nYou can get WPA (Windows Performance Analyzer) from here:`n"
		$Message += "`thttps://apps.microsoft.com/detail/9n0w1b2bxgnz`n"
		$Message += "Or the public preview:`n"
		$Message += "`thttps://apps.microsoft.com/detail/9n58qrw40dfw`n"
	}
	elseif (!$Recent)
	{
		# If Win10+ and $Exe == WPR.exe and $WPR_Path != $Env:SystemRoot\System32
		# then suggest $Env:WPT_PATH = $Env:SystemRoot\System32

		$System32 = "$Env:SystemRoot\System32"
		if ($Recent -and ($Version -ge 10) -and ($Env:WPT_PATH -ne $System32) -and ($script:WPR_Path -ne "$System32\$Exe"))
		{
			$Message += "`nIt may help to set the environment variable WPT_PATH:`n"
			if (InvokedFromCMD)
			{
				$Message += "`tset WPT_PATH=$Env:SystemRoot\System32`n"
			}
			else
			{
				$Message += "`t`$Env:WPT_PATH=`"$Env:SystemRoot\System32`"`n"
			}
			$Message += "...and try again.`nElse:`n"
		}

		$Message += "`nPlease install the Windows Performance Toolkit (WPT), part of the Windows ADK."

		if ($Version -ge 10)
		{
			$Message += "`nYou can download the `"Windows ADK`" for from here: https://aka.ms/ADK`n"
			$Message += "Install only the `"Windows Performance Toolkit`" option.`n"
			$Message += "OR:"

			# This is a large and cumbersome download.
			$Message += "`nThis is the latest (very large) `"Windows ADK - Insider Preview`":`n"
			$Message += "`thttps://aka.ms/ADK-Preview`n"
			$Message += "Sign in with your Windows Insider account. Install only the `"Windows Performance Toolkit`" option.`n"
		}
		else
		{
			$Message += "`nYou may need to install an older version of the `"Windows ADK`".`n"
			$Message += "See: https://aka.ms/ADK#other-adk-downloads`n"
			$Message += "Install only the `"Windows Performance Toolkit`" option.`n"
		}
	} # !(WPA && Win8.1+)

	$Message += "`nYou might need to set the WPT_PATH environment variable to the folder which contains $Exe"

	Write-Warn $Message
} # WriteWPTInstallMessage


<#
	Confirm that the path of the form "X:\..." and X: is a local/fixed drive.
	Does not validate the full path.
#>
function IsAbsFixedDrivePath
{
Param (
	[string]$Path
)
	if ($Path -notmatch "^[a-z]:\\") { return $False }
	$DriveInfo = [System.IO.DriveInfo]("$Path[0]")
	if (!$DriveInfo) { return $False }
	return ($DriveInfo.Drivetype -eq "Fixed")
}


<#
	Return the amount of free space, in bytes, on the given drive: "X:"
	Return $Null on error.
#>
function GetDriveFreeSpace
{
Param (
	# "X:"
	[string]$Drive
)
	if ($Drive -notmatch "^[a-z]:") { return 0 }

	$DriveInfo = Get-PSDrive $Drive.SubString(0,1) -PSProvider FileSystem -Verbose:$false
	if (!$DriveInfo) { return $Null }
	if (!(DoVerbose)) { return $DriveInfo.Free }

	# $DriveInfo.Free is very noisy when verbose.
	$VPT = $VerbosePreference
	$VerbosePreference = "SilentlyContinue"
	$DIFree = $DriveInfo.Free # 4>$Null # Verbose redirection requires: PSv3+
	$VerbosePreference = $VPT
	return $DIFree
}


<#
	If needed, transform: ".\Folder\Name.ext*" to "$ScriptHomePath\Folder\Name.ext*"
	Or transform:        "..\Folder\Name.ext*" to "$ScriptRootPath\Folder\Name.ext*"

	".\*" is relative to $ScriptHomePath
	"..\*" is relative to $ScriptRootPath

	Does no validation or canonicalization.
	Callers may invoke: Convert-Path -LiteralPath $TargetPath -ErrorAction:SilentlyContinue
#>
function MakeFullPath
{
Param (
	[string]$TargetPath
)
	if (!$TargetPath) { return $Null }
	$TargetPath = $TargetPath -replace "^\.\.\\","$(ScriptRootPathString)\"
	$TargetPath = $TargetPath -replace "^\.\\","$(ScriptHomePathString)\"
	return $TargetPath
}


<#
	Get the path of the trace file: "$script:TracePath\$InstanceName.etl"
#>
function GetTraceFilePathString
{
Param (
	[string]$TraceName
)
	if (!$script:TracePath) { EnsureTracePath; Write-Dbg "Trace path not previously initialized." }
	return "$script:TracePath\$TraceName.etl"
}


<#
	Get the path of the trace file: "PATH\NAME.etl"
	If it does not exist, display an error and exit 1.
#>
function TestTraceFilePath
{
Param (
	[string]$TraceFilePath
)
	if (Test-Path -PathType leaf -LiteralPath $TraceFilePath -ErrorAction:SilentlyContinue) { return }

	Write-Err "Error: The ETL trace file does not exist:"
	Write-Err $TraceFilePath
	if ($TraceFilePath -match '^[a-z]:[^\\]')
	{
		# Relative path: x:path\filename
		Write-Warn "Try using a full path: $($TraceFilePath.Substring(0, 2))\<Path>\$($TraceFilePath.Substring(2))"
	}
	Write-Msg "To collect a trace, please run: $(GetScriptCommand) Start [-Options]"
	Write-Msg "To open a trace in another folder, run: $(GetScriptCommand) View -Path <Path>\<Name>.etl"
	Write-Msg
	exit 1
}


<#
	An InstanceName may have an extra param encoded in it in this form:
	MSO-Trace-Heap#Snap => -Snap
#>
function GetExtraParamFromInstance
{
Param (
	[string]$InstanceName
)
	if ($InstanceName -like '*#*')
	{
		return "-$(($InstanceName -split '#')[-1])"
	}
	return $Null
}


<#
	Standard text for when (ProcessTraceCommand Stop) returns: [ResultValue]::Collected
#>
function _WriteTraceCollectedExtra
{
Param (
	[string]$TraceName,
	# Can be $Null
	[string]$ExtraParam
)
	$TraceFilePath = GetTraceFilePathString $TraceName
	if (!(Test-Path -PathType leaf -Path $TraceFilePath -ErrorAction:SilentlyContinue))
	{
		Write-Warn
		Write-Warn "The trace file was not created: $TraceName.etl"

		return $False
	}
	Write-Msg
	Write-Msg "The ETW trace is at: $(GetEnvPath $TraceFilePath)"
	Write-Msg $TraceFilePath
	Write-Msg
	Write-Msg "It may be opened with Windows Performance Analyzer (WPA)."
	Write-Msg "Or run: $(GetScriptCommand) View" $ExtraParam

	return $True
}


<#
	Standard text for when (ProcessTraceCommand Stop) returns: [ResultValue]::Collected
#>
function _WriteTraceCollected
{
Param (
	[string]$InstanceName
)
	$ExtraParam = GetExtraParamFromInstance $InstanceName
	return _WriteTraceCollectedExtra $InstanceName $ExtraParam
}


function WriteTraceCollected
{
Param (
	[string]$InstanceName
)
	$Null = _WriteTraceCollected $InstanceName
	Write-Warn
}


<#
	Check for the existence of PATH\EXE, and optionally do a test run.
	If $AltPath then skip this path ($False) and capture the next one ($True).
	Sometimes we find a WPR.exe or WPA.exe which is somehow not executable.
#>
function TestExePath
{
Param (
	[string]$ExePath,
	[bool]$TestRun,
	[ref]$AltPath
)
	if (!(Test-Path -PathType leaf -Path $ExePath -ErrorAction:SilentlyContinue)) { return $False }

	if ($TestRun)
	{
		try
		{
			$Null = & $ExePath -? 2>$Null | Out-Null # silent mode for PSv2+
			if ($?) { $TestRun = $False } # Success!
		}
		catch
		{
			# Do not flag success.
		}

		if ($TestRun)
		{
			Write-Status "Test run failed: $ExePath"
			return $False
		}
	}

	if ($AltPath.Value)
	{
		# if %WPT_PATH% then return the next path that's different.
		# Else return the 2nd viable path.

		if ($Env:WPT_PATH)
		{
			return !$ExePath.StartsWith($Env:WPT_PATH, 'CurrentCultureIgnoreCase')
		}
		else
		{
			$AltPath.Value = $False
			return $False
		}
	}

	return $True
}


<#
	Get the full path of the given executable from the Windows Performance Toolkit: WPA.exe, WPR.exe, etc.
	This path can be customized with the WPT_PATH environment variable. ($Env:WPT_PATH may get cleared if it is invalid.)
	ISSUE Win10/11+: WPR.exe exists in: $Env:windir\system32\wpr.exe  Should this script search for another (newer?) version?
#>
function GetWptExePath
{
Param (
	[string]$Exe,
	[switch]$Silent,
	[switch]$AltPath,
	[switch]$TestRun
)
	# Test the optional environment variable
	# This is set by the user. All others are set by the system or by an installer.

	if ($env:WPT_PATH)
	{
		$WptPath = $env:WPT_PATH.Trim('"').TrimEnd('\')
		$WptPath = "$WptPath\$Exe"
		if (TestExePath $WptPath $TestRun ([ref]$AltPath)) { return $WptPath }

		# Sanity check: If $env:WPT_PATH does not exist then reset is to null for this script.
		if (!(Test-Path -PathType container -Path $env:WPT_PATH -ErrorAction:SilentlyContinue)) { $env:WPT_PATH = $Null }
	}

	# Test Windows Apps (not executable stubs)

	# $Exe should exist in the folder registered under WPA.exe
	$PathReg = $Null
	$WpaOpenPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\wpa.exe"
	$WptPath = GetRegistryValue $WpaOpenPath "Path"
	if ($WptPath)
	{
		$WptPath = $PathReg = "$WptPath\$Exe"
		if (TestExePath $WptPath $TestRun ([ref]$AltPath)) { return $WptPath }
	}

	# Test the registry class

	$WpaOpenPath = "Registry::HKEY_CLASSES_ROOT\wpa\shell\open\command"
	$WptPath = GetRegistryValue $WpaOpenPath $Null

	# $Exe should exist adjacent to WPA.exe
	if ($WptPath -and ($WptPath -match ".+wpa\.exe"))
	{
		$WptPath = $Matches[0] -replace "wpa.exe",$Exe
		if (TestExePath $WptPath $TestRun ([ref]$AltPath)) { return $WptPath }
	}

	# Test various default install paths for the Windows Performance Toolkit (x86/x64)

	$PGF = ${env:ProgramFiles(x86)}
	if (!$PGF) { $PGF = $env:ProgramFiles } # 32-bit OS !?
	if ($PGF)
	{
		$WptPath = "$PGF\Windows Kits\10\Windows Performance Toolkit\$Exe"
		if (TestExePath $WptPath $TestRun ([ref]$AltPath)) { return $WptPath }

		$WptPath = "$PGF\Windows Kits\8.1\Windows Performance Toolkit\$Exe"
		if (([Environment]::OSVersion.Version -lt [Version]'10.0.0.0') -and (TestExePath $WptPath $TestRun ([ref]$AltPath))) { return $WptPath }

		# Too old!
		# "$PGF\Windows Kits\8.0\Windows Performance Toolkit\$Exe"
	}

	# App store paths require Admin privilege to walk, unless you land directly on the app folder.
	# Look up the path of the app folder(s) via the registry.

	$WildPath = 'Registry::HKEY_CLASSES_ROOT\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Microsoft.WindowsPerformanceAnalyzer*'
	[string[]] $RegPaths = Get-Item -Path $WildPath -ErrorAction:SilentlyContinue

	foreach ($RegPath in $RegPaths)
	{
		$Path = GetRegistryValue "Registry::$RegPath" 'PackageRootFolder'
		if (!$Path -or !($Path -is [string])) { continue }

		$WptCmd = Get-ChildItem -Recurse -Path $Path -Filter $Exe | Select-Object -First 1
		if ($WptCmd)
		{
			$WptPath = $WptCmd.FullName
			if (($WptPath -ne $PathReg) -and (TestExePath $WptPath $TestRun ([ref]$AltPath))) { return $WptPath }
		}
	}

	# Test the system path, which may include WindowsApps (executable stubs, NO VERSION)

	$PathApps= $Null
	$WptCmd = Get-Command -TotalCount 1 -CommandType Application $Exe -ErrorAction:SilentlyContinue
	if ($WptCmd)
	{
		$WptPath = $PathApps = $WptCmd.Path
		if (TestExePath $WptPath $TestRun ([ref]$AltPath)) { return $WptPath }
	}

	# Search the installed WindowsApps store (executable stubs, NO VERSION)

	if ($Env:LOCALAPPDATA)
	{
		$WptPath = "$Env:LOCALAPPDATA\Microsoft\WindowsApps\$Exe"
		if (($WptPath -ne $PathApps) -and (TestExePath $WptPath $TestRun ([ref]$AltPath))) { return $WptPath }
	}

	# Try the Side-by-Side path for WPR

	if ($Exe -eq 'WPR.exe')
	{
		$WildPath = "$Env:WinDir\WinSxS\$(GetProcessorArch)_microsoft-windows-coresystem-wpr_*"
		[string[]] $Paths = Get-Item -Path $WildPath -ErrorAction SilentlyContinue
		foreach ($Path in $Paths)
		{
			$WptPath = "$Path\$Exe"
			if (TestExePath $WptPath $TestRun ([ref]$AltPath)) { return $WptPath }
		}
	}

	#
	# After this point we can't identify the .exe module in advance, so we can't determine its version.
	# This affects our ability to correctly choose parameters, etc.
	#

	# Test $env:WPT_Path for *.bat, etc.

	if ($env:WPT_PATH)
	{
		$WptPath = $env:WPT_PATH.Trim('"').TrimEnd('\')
		$WptPath = "$WptPath\$Exe"
		[string[]]$PathStar = ($WptPath -replace '.exe$','.bat'),($WptPath -replace '.exe$','.cmd')
		$WptPath = Get-Command -TotalCount 1 -CommandType Application -Name $PathStar -ErrorAction:SilentlyContinue
		if ($WptPath -and (TestExePath $WptPath.Path $TestRun ([ref]$AltPath))) { return $WptPath.Path }
	}

	# Test the system path for *.bat, etc.

	[string[]]$ExeStar = ($Exe -replace ".exe$","?.bat"),($Exe -replace ".exe$","?.cmd")
	$WptPath = Get-Command -TotalCount 1 -CommandType Application -Name $ExeStar -ErrorAction:SilentlyContinue
	if ($WptPath -and (TestExePath $WptPath.Path $TestRun ([ref]$AltPath))) { return $WptPath.Path }

	# Let the OS decide what to do.

	$WptPath = $Exe -replace ".exe"

	if (!$Silent)
	{
		Write-Msg
		Write-Warn "Warning: Could not find: $WptPath"
		WriteWPTInstallMessage $Exe
		Write-Msg
	}

	return $WptPath
} # GetWptExePath


<#
	Return true if the current OS version >= ($Major.$Minor.$Build)
#>
function CheckOSVersion
{
Param (
	[Version]$VersionReference
)
	$Environ = [Environment]::OSVersion.Version
	$Result = [version]$Environ -ge $VersionReference
	if (!$Result) { Write-Status "OS Version = $Environ" }
	return $Result
}


<#
	Return $True if the version is non-null and not a default version.
#>
function IsRealVersion
{
Param (
	[Version]$Version
)
	return !(!$Version -or ($Version.Build -eq 99999))
}


<#
	Return a default version for files which exist but have no version info.
	The Build number will be: 99999
#>
function DefaultVersion
{
	# When the FileVersion is not to our liking, use this default version instead.
	$OSV = [Environment]::OSVersion.Version
	[Version]$DefaultVersion = "$($OSV.Major).$($OSV.Minor).99999"
	if (($OSV.Major -eq 10) -and ($OSV -ge [Version]'10.0.22000'))
	{
		$DefaultVersion = [Version]'11.0.99999'
	}
	return $DefaultVersion
}


<#
	Return a [Version]X.Y.Z based on the [FileVersionInfo].
		$Null -> nonexistent
		Major.Minor.99999 -> exists but no real version info
#>
function FileVersionFromFileInfo
{
Param (
	[Diagnostics.FileVersionInfo]$FileInfo
)
	if ((!$FileInfo) -or (!$FileInfo.FileName)) { return $Null }

	$FileName = $(Split-Path -Leaf -Path $FileInfo.FileName -ErrorAction:SilentlyContinue)

	if ($FileInfo.FileVersion)
	{
		$VersionRegEx = "^\d+\.\d+\.\d+"

		if ($FileInfo.FileVersion -match $VersionRegEx) # "W.X.Y.Z (Description)"
		{
			[Version]$FileVersion = $Matches[0]
		}
		elseif ($FileInfo.ProductVersion -match $VersionRegEx)
		{
			[Version]$FileVersion = $Matches[0]
		}
		else
		{
			[Version]$FileVersion = (DefaultVersion)
		}

		if (($FileVersion.Major -eq 0) -and ($FileVersion.Minor -gt 0))
		{
			[Version]$FileVersion = (DefaultVersion) # Beta v0.X.X
		}

		Write-Status "Version Check: $FileName - FileVersion $($FileInfo.FileVersion) - (v$FileVersion)`n" (ReplaceEnv $FileInfo.FileName) # path
	}
	else
	{
		$FileVersion = (DefaultVersion)

		# The Windows Store version of WPA has no file version in its launcher: wpa.exe  Likewise: WPA.bat
		Write-Status "Version Check: $FileName - No file version available. Assuming v$FileVersion`n" (ReplaceEnv $FileInfo.FileName) # path
	}

	return $FileVersion
} # FileVersionFromFileInfo


<#
	Get the file or product version info of the given executable file as [Version]X.Y.Z
	Return $Null if there is no version info, or missing or invalid file.
#>
function GetFileVersion
{
Param (
	[string]$TargetPath
)
	$CommandInfo = Get-Command -TotalCount 1 -CommandType Application -ErrorAction:SilentlyContinue $TargetPath

	if (!$CommandInfo)
	{
		Write-Status "Application not found: $TargetPath"
		return $Null
	}

	if ($CommandInfo -is [Management.Automation.ApplicationInfo])
	{
		$CommandInfo = $CommandInfo.FileVersionInfo
	}

	if ($CommandInfo -is [Diagnostics.FileVersionInfo])
	{
		return FileVersionFromFileInfo $CommandInfo
	}

	Write-Status "No version found: $TargetPath"
	return $Null
}


<#
	"DisablePagingExecutive" is only for 64-bit Windows 7 or Server 2008-R2.
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options
#>
function CheckPagingExecutive
{
	if ((GetProcessorArch) -eq "AMD64")
	{
		# Windows 7 is v6.1 (less than v6.2.0000)

		if (!(CheckOSVersion '6.2.0'))
		{
			# Test the value before setting and warning.
			# Ideally WPR should do this!

			$MemManPath = "HKLM:\System\CurrentControlSet\Control\Session Manager\Memory Management"
			$Value = GetRegistryValue $MemManPath "DisablePagingExecutive"
			if (!$Value) # 0 or $Null
			{
				$Result = InvokeWPR -DisablePagingExecutive on
				Write-Msg $Result
				Write-Msg "`n`tOr to disable this, run:"
				Write-Msg "`"$script:WPR_Path`" -DisablePagingExecutive off"
				Write-Msg
			}
		}
	}
} # CheckPagingExecutive


<#
	The PreWin10 script-set works with older versions of PowerShell (v2+), WPR, and WPA.
#>
function RelaunchAsPreWin10
{
	# Relaunch using the PreWin10 version of the scripts.
	$PreWin10Script = "$(ScriptPreWin10Path)\$($script:MyInvocation.MyCommand)"

	if (!(Test-Path -PathType leaf -Path $PreWin10Script -ErrorAction:SilentlyContinue))
	{
		Write-Err "Error: Cannot find path:" $PreWin10Script
		Write-Err "Not continuing with this older version of WPR:`n`"$script:WPR_Path`""
		exit 3
	}

	Write-Warn "Running: $PreWin10Script" @PSScriptParams
	& $PreWin10Script @PSScriptParams >$Null
	exit $LastExitCode
}


<#
	Check whether extra setup needs to happen before executing WPR commands.
#>
function CheckPrerequisites
{
Param(
	[switch]$NoAdminCheck
)
	if ($script:WPR_Path) { return }

	# Ensure $script:TracePath for traces and intermediate files.

	EnsureTracePath

	# Get the path for WPR
	# Check whether we can successfully invoke wpr.exe

	$script:WPR_Path = GetWptExePath "wpr.exe" -TestRun

	$script:WPR_Win10Ver = $Null # WPR's Win10 version info
	$script:WPR_PreWin10 = $True
	$script:WPR_Flushable = $False

	if (IsModernScript)
	{
		$Version = GetFileVersion $script:WPR_Path

		if (!$Version) { $Version = [Version]'10.0.0' } # Punt for now. Could be WPR.bat

		if ($Version -lt [Version]'6.3.0')
		{
			# The Windows 8.1 ADK/WPT/SDK (v6.3) works on: Win7/8.0/8.1
			# Versions earlier than that will not work here.  Please install a newer one.

			Write-Err "A very old version of the Windows Performance Toolkit was found here:`n$script:WPR_Path"
			Write-Err "Please uninstall it.  Then..."
			WriteWPTInstallMessage "wpr.exe"
			exit 1
		}

		if ($Host.Version.Major -le 2)
		{
			Write-Warn "Using an old version of PowerShell (v2)."
			RelaunchAsPreWin10
		}

		if ($Version -lt [Version]'10.0.0')
		{
			Write-Warn "Using an older version of the Windows Performance Recorder (WPR)."
			RelaunchAsPreWin10
		}

		# Earlier versions of WPR are not able to process certain WPRP tags, such as <stacks>.
		# See ValidateRecordingProfileString.

		$script:WPR_Win10Ver = $Version
		$script:WPR_PreWin10 = $False

		# For TraceMemory: WPR -MarkerFlush
		# -MarkerFlush was available in: WPR [v10.0.17709 - v10.0.20153)
		$script:WPR_Flushable = ($Version -ge [Version]'10.0.17709') -and ($Version -lt [Version]'10.0.20153')
	}
	else
	{
		# Check whether the PagingExecutive should be disabled. (Rare!)
		CheckPagingExecutive
	}

	if ($NoAdminCheck) { return }

	# Check for Admin privileges.

	if (!(CheckAdminPrivilege))
	{
		Write-Err "Please re-run this script with Administrator privileges."
		exit 1
	}
} # CheckPrerequisites


<#
	Some of the .wpap/.wprp files have the minimum version in their name: Network.wprp and Network.15002.wprp
	Get a list of candidate files in descending order: Network.23456.wprp Network.12345.wprp ...
	Return just the name of the first one where (file_name_min_version <= $VersionLimit), else $Null.
#>
function GetVersionedFileName
{
Param(
	[string]$Path,
	[string]$Ext, # no dot
	[Version]$VersionReference
)
	$PathWild = $Path -replace ".$ext`$",".?????.$ext"
	$VersionNameList = Get-ChildItem -path $PathWild -Name | Sort-Object -descending
	if (!$VersionNameList) { return $Null } # PSv2

	foreach ($VersionName in $VersionNameList)
	{
		# Get the chars after the first "." in the file name (the version#).
		$Version = ($VersionName -split '\.')[1]
		if ($Version -match "\d{5}")
		{
			if ($VersionReference -ge [Version]"10.0.$Version") { return $VersionName }
		}
	}
	return $Null
} # GetVersionedFileName


<#
	Remove the logo text from the output of WPR, etc.
	Returns $Null on failure.
	$LastExitCode is the number of lines.
#>
function StripLogo
{
Param (
	[string]$Result
)
	# Remove blank lines and those containing "Microsoft".
	$Split = ($Result -Split "`n") | ? { $_ -notmatch "Microsoft|^\s*$" }

	if ($Split)
	{
		$global:LastExitCode = $Split.Count
		return $Split | Out-String
	}

	$global:LastExitCode = 0
	return $Null
}


<# This is not one of WPR's built-in profiles if it has a path and/or an extension and/or a profile name or .Verbose/.Light. #>
function IsNotBuiltinProfile { Param([string]$Profile) return ($Profile -match "[\.!]") }

<# Strip any trailing "!ProfileName" or ".Verbose" or ".Light" #>
function StripProfileName { Param([string]$Profile) return ($Profile -replace "(!.+)|(.Verbose)|(.Light)$") }

<# Return only a trailing "ProfileName" #>
function GetProfileName
{
Param (
	[string]$Profile
)
	if ($Profile -like "*!*") { return ($Profile -replace "^.+!") }
	if ($Profile -like "*.Verbose") { return "Verbose" }
	if ($Profile -like "*.Light") { return "Light" }
	return $Null
}


<#
	Return FileName.wprp.Light/Verbose or FileName.wprp!ProfileName
#>
function CreateProfileName
{
Param (
	[string]$WprFileNew,
	[string]$ProfileName
)
	if ($WPR_PreWin10) { return "$WprFileNew.$ProfileName" } # Only .Light or .Verbose
	return "$WprFileNew!$ProfileName"
}


<#
	Return a valid Recording Profile string, or $Null.
		"<path>\<file>.wprp!ProfileName"
		".\WPRP\<file>.wprp!ProfileName" # Convert to full path.
		"<BuiltInProfile>"

	Likewise for <some_path>\MyProfile.wprp.Verbose/.Light

	".\*" is relative to $ScriptHomePath
	"..\*" is relative to $ScriptRootPath
#>
function ValidateRecordingProfileString
{
Param (
	[string]$Profile
)
	if (!$Profile) { return $Null } # PSv2

	$ProfileOrig = $Profile.Trim()
	$Profile = MakeFullPath $ProfileOrig

	if (IsNotBuiltinProfile $Profile)
	{
		$ProfilePath = StripProfileName $Profile

		# It has a valid path?
		if (!(Test-Path -PathType leaf -Path $ProfilePath -ErrorAction:SilentlyContinue))
		{
			Write-Warn "Warning: Cannot find: `"$ProfilePath`""
			Write-Warn "Ignoring this recording profile."
			Write-Msg
			return $Null
		}

		$ProfileFile = (Get-Item $ProfilePath).Name

		if ($WPR_PreWin10)
		{
			# Pre-Win10 WPR allows no profile name.

			if (($ProfilePath -ne $Profile) -and ($Profile -notmatch "(.Verbose)|(.Light)$"))
			{
				Write-Warn "Warning: This version of WPR does not accept a ProfileName."
				Write-Warn "Only: $ProfileFile OR $($ProfileFile).Verbose OR $($ProfileFile).Light"
				Write-Msg
				return $Null
			}
		}
		else
		{
			# Win10 WPR requires a profile name, the way we're using it.

			if ($ProfilePath -eq $Profile)
			{
				Write-Warn "Warning: This recording profile needs a profile name:`n$ProfilePath!<ProfileName>"

				$Result = InvokeWPR -Profiles $ProfilePath
				$Result2 = $Null
				if ($global:LastExitCode -eq 0) { $Result2 = StripLogo $Result } # Sets $LastExitCode

				if ($Result2)
				{
					Write-Warn (Ternary ($global:LastExitCode -eq 1) "The following will be used:" "One of the following will be used:")
					Write-Warn $Result2
				}
				else
				{
					Write-Status $Result
					Write-Warn "Ignoring this recording profile."
					Write-Warn "To see a list of profile names, run:`nWPR -profiles `"$ProfilePath`""
					Write-Msg
					return $Null
				}
			}
		}

		if (($script:WPR_Win10Ver) -and ($Profile -ne $ProfileOrig)) # This form only: .\WPRP\Profile.wprp!ProfileName
		{
			# WPR earler than v10.0.XXXXX is not able to process certain WPRP tags, such as <stacks>.
			# Search for .\WPRP\Profile.XXXXX.wprp such that: XXXXX <= WPR_Win10Ver.Build

			$VersionedProfileName = GetVersionedFileName $ProfilePath "wprp" $script:WPR_Win10Ver
			if ($VersionedProfileName)
			{
				Write-Status "Using $VersionedProfileName with WPR v$($script:WPR_Win10Ver)"
				$Profile = $Profile -replace $ProfileFile,$VersionedProfileName
			}
		}
	}
	return $Profile
} # ValidateRecordingProfileString


<#
	Make an educated guess as to whether the identified ETW Provider should trace with non-paged memory.
	Must return only: $True, $False

	NonPagedMemory:
	"You must set this true if the provider is running in kernel such as the driver."
	"Though the attribute is for the provider, the property applies to the whole session."
	https://devblogs.microsoft.com/performance-diagnostics/authoring-custom-profile-part3/#:~:text=NonPagedMemory
#>
function UseNonPagedMemory
{
Param (
	[string]$GuidOrName
)
	# Short Circuit Heuristics for NonPagedMemory
	if ($GuidOrName -like '*Kernel*') { return $True }
	if ($GuidOrName -like '*Win32K')  { return $True }

	# TraceLogging providers usually have dots: Microsoft.Windows.WindowsErrorReporting
	if ($GuidOrName -like '*.*') { return $False }

	$key = GetETWManifestRegistryKey $GuidOrName

	if (!$key) { return $False }

	[string]$ResFilePath = $key.GetValue('ResourceFileName')
	if ([string]::IsNullOrEmpty($ResFilePath)) { return $False }

	$ResFile = Split-Path -Leaf -Path $ResFilePath
	$Guid = $key.PSChildName # {GUID} = the key name
	$Name = $key.GetValue($Null)

	# Write: Name {GUID} Module
	Write-Status "$Name $Guid $ResFile"

	if ($ResFile -like '*.sys') { return $True } # Kernel Driver
	if ($ResFile -like '*Windows-System*') { return $True } # Microsoft-Windows-System-Events.dll
	if ($ResFile -like '*windows-kernel*') { return $True } # microsoft-windows-kernel-power-events.dll

	if ($Name -like '*Kernel*') { return $True } # Test the registry's ETW Provider name for 'Kernel'.
	if ($Name -like '*Integrity') { return $True } # Microsoft-Windows-CodeIntegrity operates within the Kernel.

	return $False
}


<#
	Transform a plus-separated ProviderString using XPerf syntax: NameOrGUID:KeywordFlags:Level:'stack'[+ ...]
	into a simple but well-formatted WPRP file, and return $Null or: <Path>!<ProfileName>
#>
function WPRPFromProviderString
{
Param (
	[string]$ProviderString
)
	$_BUFFERS_      = 16 # MB # This buffer size should work well with all but the most verbose providers.
	$_EC_NAME_      = 'MSO Event Collector' # All event collectors should have this same name so that WPR will merge them into one.
	$_WPRP_NAME_    = 'WPR_XPERF_Profile.wprp'
	$_PROFILE_NAME_ = 'AUX_Profile'
	$_DESCRIPTION_  = 'Auxiliary Profile Created by MSO-Scripts'

	# Replace these tokens with the actual values.
	$_ID_ = '_ID_'
	$_NPM_ = '_NPM_' # NonPagedMemory
	$_NAME_ = '_NAME_' # or Provider GUID
	$_LEVEL_ = '_LEVEL_'
	$_STACK_  = '_STACK_'
	$_KEYWORD_ = '_KEYWORD_'
	$_PROVIDER_ = '_PROVIDER_'

	$ProviderString = $ProviderString -replace '[<>]','' # xml scrub: remove <brackets>
	$ProviderStringComment = $ProviderString -replace '--','__' # xml scrub: remove dbl-hyphen

	$ScriptPath = $(if ($PSCommandPath) {$PSCommandPath} else {$MyInvocation.ScriptName})
	$ScriptName = $(Split-Path -Path $ScriptPath -Leaf)
	$FuncName = $MyInvocation.MyCommand.Name

	$_Preamble =  "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n" +
	              "<!-- Automatically generated by MSO-Scripts via: $ScriptName!$FuncName -->`r`n" +
	              "<!-- From XPerf Format: $ProviderStringComment -->`r`n" +
	              "<WindowsPerformanceRecorder Version=`"1`" Author=`"MSO-Scripts`" >`r`n" +
	              "  <Profiles>`r`n"
	$_Collector = "    <EventCollector Id=`"EC_Basic`" Name=`"$_EC_NAME_`">`r`n" +
	              "      <BufferSize Value=`"1024`" />`r`n" +
	              "      <Buffers Value=`"$_BUFFERS_`" />`r`n" +
	              "    </EventCollector>`r`n`r`n"
	$_Profile =   "    <Profile Name=`"$_PROFILE_NAME_`" Description=`"$_DESCRIPTION_`"`r`n" +
	              "     DetailLevel=`"Light`" LoggingMode=`"File`" Id=`"$_PROFILE_NAME_.Light.File`">`r`n" +
	              "      <Collectors Operation=`"Add`">`r`n"
	$_Providers = "        <EventCollectorId Value=`"EC_Basic`">`r`n" +
	              "          <EventProviders Operation=`"Add`">`r`n`r`n"
	$_Comment =   "            <!-- $_PROVIDER_ -->`r`n"
	$_Provider =  "            <EventProvider Id=`"$_ID_`" Name=`"$_NAME_`" Level=`"$_LEVEL_`" NonPagedMemory=`"$_NPM_`" Stack=`"$_STACK_`">`r`n"
	$_Keyword =   "              <Keywords> <Keyword Value=`"$_KEYWORD_`" /> </Keywords>`r`n"
	$_ProviderX = "            </EventProvider>`r`n`r`n"
	$_ProvidersX ="          </EventProviders>`r`n" +
	              "        </EventCollectorId>`r`n"
	$_ProfileX =  "      </Collectors>`r`n" +
	              "    </Profile>`r`n`r`n"
	$_ProfMemory ="    <Profile Name=`"$_PROFILE_NAME_`" Description=`"$_DESCRIPTION_`"`r`n" +
	              "     DetailLevel=`"Light`" LoggingMode=`"Memory`" Base=`"$_PROFILE_NAME_.Light.File`" Id=`"$_PROFILE_NAME_.Light.Memory`" />`r`n`r`n"
	$_Postamble = "  </Profiles>`r`n" +
	              "</WindowsPerformanceRecorder>"

	[string[]]$Providers = $ProviderString -split '\+'
	if ($Providers.Count -eq 1)
	{
		# Just in case, allow the intuitive semi-colon separator.
		$Providers = $ProviderString -split ';'
	}

	$Count = 0
	$_WPRP = $_Preamble + $_Collector + $_Profile + $_Providers

	foreach ($Provider in $Providers)
	{
		[string[]]$Elements = $Provider -split ':'
		if (($Elements -eq $Null) -or ($Elements.Count -gt 4))
		{
			Write-Warn "Each provider string requires up to 4 XPerf-style, semi-colon separated values: GuidOrName:KeywordFlags:Level:'Stack'"
			return $Null
		}

	<#
		[0]: GUID or Name String (required)
		[1]: KeywordFlags: Long (hex or decimal)
		[2]: Level: 1-255
		[3]: 'stack' or Stack
	#>
		[string]$GuidOrName = $Elements[0].Trim(' {}')
		[long]$Keyword = 0 # Unknown
		[byte]$Level = 5 # Verbose (default)
		[bool]$Stack = $False

		if (!$GuidOrName)
		{
			Write-Warn "Missing Registered Provider Name or GUID"
			return $Null
		}

		# Keyword

		if (($Elements.Count -gt 1) -and $Elements[1])
		{
			$Keyword = $Elements[1] -as [long]
			if (!$Keyword)
			{
				# 0 or failed to parse!
				Write-Warn "Invalid KeywordFlags:" $Elements[1]
				return $Null
			}
		}

		# Level

		if (($Elements.Count -gt 2) -and $Elements[2])
		{
			$Level = $Elements[2] -as [byte]
			if (!$Level)
			{
				# 0 or failed to parse!
				Write-Warn "Invalid Level:" $Elements[2]
				return $Null
			}
		}

		# Stack

		if (($Elements.Count -gt 3) -and $Elements[3])
		{
			if (($Elements[3].Trim(" '")) -ne 'stack')
			{
				Write-Warn "Invalid Stack Indicator:" $Elements[3]
				return $Null
			}
			$Stack = $True
		}

		$Count++
		$Id = "EP_$Count"
		$NPM = UseNonPagedMemory $GuidOrName
		$ProviderComment = $Provider -replace '--','__' # xml scrub: remove dbl-hyphen

		$_WPRP += $_Comment -replace $_PROVIDER_,$ProviderComment
		$_WPRP += $_Provider -replace $_NAME_,$GuidOrName -replace $_LEVEL_,[string]$Level -replace $_STACK_,$Stack.ToString().ToLower() -replace $_NPM_,$NPM.ToString().ToLower() -replace $_Id_,$Id
		if ($Keyword) { $_WPRP += $_Keyword -replace $_KEYWORD_,('0x{0:X}' -f $Keyword) }
		$_WPRP += $_ProviderX
	}

	if (!$Count)
	{
		Write-Warn "There were no providers found in WPT_XPERF."
		return $Null
	}

	$_WPRP += $_ProvidersX + $_ProfileX + $_ProfMemory + $_Postamble

	EnsureTracePath
	$File = New-Item -Path $script:TracePath -Name $_WPRP_NAME_ -Type "file" -Force -Value $_WPRP -ErrorAction:SilentlyContinue -ErrorVariable:FileError

	if (!$File)
	{
		Write-Err $FileError
		return $Null
	}

	$ProfileOut = "$File!$_PROFILE_NAME_"
	if ($script:WPR_PreWin10) { $ProfileOut = $File }

	Write-Status "Created temporary WPR Profile: $(ReplaceEnv $ProfileOut)"

	return $ProfileOut
}


<#
	Turn the environment variables WPT_WPRP and WPT_XPERF into WPR recording profiles.

	WPT_WPRP:  Semi-colon separated: <path>\<file>.wprp!<ProfileName>
	WPT_XPERF: Plus-separated: NameOrGUID:KeywordFlags:Level:'stack'[+...]

	Return a corresponding array of WPR-ready profiling commands:
		-Start c:\MSO-Scripts\WPRP\SomeProfile.wprp!ProfileName

	Skip profiles of any other format with a warning.
#>
function PrepAuxRecordingProfiles
{
	[array]$ReturnProfiles = @()

	if ($Env:WPT_WPRP)
	{
		[string]$ProfileListOut = $Null
		[string[]]$ProfileListEnv = $Env:WPT_WPRP -split ';'

		foreach ($Profile in $ProfileListEnv)
		{
			$ProfileV = ValidateRecordingProfileString $Profile
			if ($ProfileV)
			{
				$ReturnProfiles += GetArgs -Start $ProfileV
				$ProfileListOut += "`n`t $ProfileV"
			}
			else
			{
				Write-Warn "Ignoring WPT_WPRP = `"$Profile`""
			}
		}

		if ($ReturnProfiles)
		{
			Write-Info "Adding WPR Profile(s) from environment variable WPT_WPRP: $ProfileListOut"
		}
		else
		{
			Write-Warn "Adding no WPR Profiles from environment variable WPT_WPRP."
		}
	}

	if ($Env:WPT_XPERF)
	{
		[string[]]$ProfileXPerfEnv = WPRPFromProviderString $Env:WPT_XPERF
		if ($ProfileXPerfEnv)
		{
			$ReturnProfiles += GetArgs -Start $ProfileXPerfEnv

			Write-Info "Adding ETW Provider(s) from environment variable WPT_XPERF:"
			foreach ($entry in ($Env:WPT_XPERF -split '\+'))
			{
				Write-Info "`t" $entry.Trim()
			}
		}
		else
		{
			Write-Warn "Ignoring environment variable: WPT_XPERF=`n" $Env:WPT_XPERF
			Write-Warn "Expected plus-separated format: NameOrGUID:KeywordFlags:Level:Stack[+...]"
			Write-Warn "Example: WPT_XPERF=`ne53c6823-7bb8-44bb-90dc-3f86090d48a6:0x00A4:4:Stack + Microsoft-Windows-RPC:::Stack + Microsoft-Windows-TCPIP"
		}
	}

	return $ReturnProfiles
}


<#
	Accept an array of additional trace collection profiles ("recording profiles") in any of these forms:
		".\WPRP\SomeProfile.wprp!ProfileName"
		"<full_path>\MyProfile.wprp!ProfileName"
		"BuiltInProfile"

	Likewise for <some_path>\MyProfile.wprp.Verbose/.Light

	".\*" is relative to $ScriptHomePath
	"..\*" is relative to $ScriptRootPath

	Return a corresponding array of WPR-ready profiling commands:
		-Start c:\MSO-Scripts\WPRP\SomeProfile.wprp!ProfileName
		-Start d:\OtherPath\MyProfile.wprp!ProfileName
		-Start BuiltInProfile

	Skip profiles of any other format with a warning.
#>
function PrepRecordingProfiles
{
Param (
	[string[]]$ProfileList,
	[switch]$IsBaseList
)
	[array]$ReturnProfiles = @()

	$FirstFail = $Null
	foreach ($Profile in $ProfileList)
	{
		$Profile = ValidateRecordingProfileString $Profile

		if (!$Profile)
		{
			if (!$FirstFail) { $FirstFail = $ForEach.Current }
			continue
		}

		if ($IsBaseList -and ($ForEach.Current -eq $ProfileList[0]))
		{ Write-Status "Base recording profile: $Profile" }
		else
		{ Write-Status "Adding recording profile: $Profile" }

		$ReturnProfiles += GetArgs -Start $Profile
	}

	if ($IsBaseList -and ($FirstFail -eq $ProfileList[0]))
	{
		Write-Err "Error: The base recording profile is not valid:" $FirstFail
		exit 1
	}

	return $ReturnProfiles
} # PrepRecordingProfiles


<#
	Do everything required to process a trace command.
	For heap tracing, first call: PrepareHeapTraceCommand
#>
function ProcessTraceCommand
{
Param (
	# Start, Stop, Cancel, Status, View
	[string]$Command,

	# Array of additional trace collection profiles in any of these forms:
	#	".\WPRP\SomeProfile.wprp!ProfileName" or "..\WPRP\SomeProfile.wprp!ProfileName"
	#	"<full_path>\MyProfile.wprp!ProfileName"
	#	"BuiltInProfile"
	# ".\*" is relative to $ScriptHomePath
	# "..\*" is relative to $ScriptRootPath
	[Parameter(Mandatory=$true)] [string[]]$RecordingProfiles,

	# Arbitrary, descriptive name for the profile instance and the ETL trace file.
	# If it ends with a #Switch, reports '-Switch' with the WPR command.
	[Parameter(Mandatory=$true)] [string]$InstanceName,

	# Optional manifest files of ETW providers to register.
	[Parameter(Mandatory=$false)] [string[]]$ProviderManifests,

	# Circular buffer mode uses a block of memory rather than infinite disk.
	[switch]$Loop,

	# Configure the autologger to trace System Restart.
	[switch]$Boot,

	# Enable Common Language Runtime (CLR, C#) providers for symbolic resolution.
	[switch]$CLR,

	# Enable JavaScript providers for symbolic resolution.
	[switch]$JS
)
	Write-Status "Command = $Command"

	if ($Command -eq "View")
	{
		ListRunningProfiles

		return [ResultValue]::View
	}

	# All commands after this point require running WPR within Admin privileges.

	CheckPrerequisites

	# The name of the trace will be $TraceName.etl
	$TraceName = $InstanceName
	$ExtraParam = GetExtraParamFromInstance $InstanceName
	$ExtraParam2 = $ExtraParam

	[array]$WprParams = $Null

	if ($Boot)
	{
		$WPRParams += '-BootTrace'
		$InstanceName += '.Boot' # Distinguish a boot trace. cf. NameFromScriptInstance
		$ExtraParam2 += Ternary (!$ExtraParam2) '-Boot' ' -Boot'
	}

	switch ($Command)
	{

	"Start"
	{
		# Invoke only one !ProfileName from each .wprp file.

		$WprParams += PrepRecordingProfiles -IsBaseList $RecordingProfiles

		$WprParams += PrepAuxRecordingProfiles # From optional environment variables

		if ($WPR_PreWin10)
		{
			if ($CLR) { $WprParams += PrepRecordingProfiles ".\WPRP\CLR.wprp" } # MSO-Scripts\PreWin10\WPRP\CLR.wprp
			if ($JS)  { $WprParams += PrepRecordingProfiles ".\WPRP\JS.wprp"  }
		}
		else
		{
			if ($CLR) { $WprParams += PrepRecordingProfiles "..\WPRP\CLR.wprp!CLR" } # MSO-Scripts\WPRP\CLR.wprp!CLR
			if ($JS)  { $WprParams += PrepRecordingProfiles "..\WPRP\JS.wprp!JS"   }

			# Add special boot tracing providers if not already in use.
			# https://github.com/microsoft/MSO-Scripts/wiki/Analyze-Windows-Boot#chart

			if ($Boot -and !($WprParams -like "*\WindowsProviders.*"))
				{ $WprParams += PrepRecordingProfiles "..\WPRP\WindowsProviders.wprp!WindowsStart" }
		}

		switch ($Env:WPT_Mode)
		{
		$Null { break }

		'Shutdown' {
			if (!$Boot)
			{
				Write-Warn "WPT_Mode: Able to trace System Shutdown."
				if ($Loop) { Write-Warn "Ignoring: -Loop"; $Loop = $False }
				if ($WprParams -notlike "*\WindowsProviders.*")
					{ $WprParams += PrepRecordingProfiles "..\WPRP\WindowsProviders.wprp!WindowsShutdown" }
				$WPRParams += '-Shutdown'
			}
			else
			{
				Write-Warn "-Boot: Ignoring WPT_Mode=Shutdown"
			}

			Write-Warn "See: https://github.com/microsoft/MSO-Scripts/wiki/Analyze-Windows-Boot#shutdown"
			break
		}

		default { Write-Warn "Unrecognized WPT_Mode: '$Env:WPT_Mode'"; break }
		}

		if ($Loop)
		{
			# Not adding -FileMode

			Write-Status "Logging to Circular Memory Buffer"
		}
		else
		{
			$LogPath = $script:TracePath
			if (DoVerbose)
			{
				$LogDrive = Split-Path -Path $LogPath -Qualifier
				$DriveFreeSpace = GetDriveFreeSpace $LogDrive
				if ($DriveFreeSpace)
				{
					$FreeSpace = [int]($DriveFreeSpace / 1GB)
					Write-Status "Logging to disk drive $LogDrive - Free Space: $FreeSpace+ GB"
				}
				else
				{
					Write-Status "Logging to: $LogPath"
				}
			}
			$WprParams += GetArgs -FileMode -RecordTempTo $LogPath
		}
		$WprParams += GetArgs -InstanceName $InstanceName

		if ($Boot) { $WPRParams = $WPRParams -replace '-Start','-AddBoot' }

		$Result = InvokeWPR @WprParams

		switch ($LastExitCode)
		{

		0
		{
			if ($Result) { Write-Msg $Result }

			ListRunningProfiles # Any other traces already running?

			SetProfileStartTime $InstanceName

			if ($JS)
			{
				Write-Info "To enable JavaScript profiling, the app may require special parameters."
				Write-Info "See: https://github.com/microsoft/MSO-Scripts/wiki/Symbol-Resolution#javascript"
			}

			if ($Boot)
			{
				Write-Msg
				Write-Msg "The ETW AutoLogger has been configured to trace the next Windows Restart."
				Write-Action "Now restart the device."
				Write-Action "Then run: $(GetScriptCommand) Stop $ExtraParam2 [-WPA]"
				Write-Msg "To restart in 10 sec., you can run: shutdown -r -t 10"
				return [ResultValue]::Success
			}

			return [ResultValue]::Started
		}

		0xc5583014 # Event Collector In Use
		{
		#TODO: Automatically do something like this (then retry): logman stop "NT Kernel Logger" -ets

			Write-Err "Error 0xc5583014: A data collector is already in use."
			Write-Err "Something else may have monopolized the ETW provider."

			ListRunningProfiles

			HandleCollectorInUse

			return [ResultValue]::Error
		}

		0xc5583001 # The profiles are already running.
		{
			Write-Warn "The WPR profiles are already running. (0xc5583001)"
			Write-Warn "Run: $(GetScriptCommand) Stop $ExtraParam2"
			Write-Warn "OR:  $(GetScriptCommand) Cancel $ExtraParam2"

			ListRunningProfiles

			# Don't cancel/reset anything!
			return [ResultValue]::Success
		}

		0x800705aa # Insufficient system resources...
		{
			HandleInsufficientResources

			ListRunningProfiles

			return [ResultValue]::Error
		}

		0xc5580612 # An Event session cannot be started without any providers.
		{
			Write-Err "`nWPR returned error 0xc5580612: An Event session cannot be started without any providers."

			HandleWPRCompatibility $WprParams

			ListRunningProfiles

			return [ResultValue]::Error
		}

		0xC558300C # A runtime state provider is not running.
		{
			Write-Err "`nWPR returned error 0xC558300C: A runtime state provider is not running."

			HandleWPRCompatibility $WprParams

			ListRunningProfiles

			return [ResultValue]::Error
		}

		0x800700b7 # Cannot create a file when that file already exists.
		{
			HandleFileConflict $WprParams

			ListRunningProfiles

			return [ResultValue]::Error
		}

		0x80070008 # Not enough memory resources are available to process this command.
		{
			HandleOOM $WprParams

			ListRunningProfiles

			return [ResultValue]::Error
		}

		default
		{
			Write-Err $script:WPR_Path @WprParams

			if (!$Result) { $Result = "WPR returned this result:" }
			Write-Err $Result "`n`t" ('(0x{0:X8})' -f $LastExitCode)

			ListRunningProfiles

			return [ResultValue]::Error
		}
		} # switch $LastExitCode

	} # Start

	"Stop"
	{
		Write-Msg "`nProcessing..."

		# Ensure that the primary Office ETW Provider is registered.
		# The TimeLine view derives from the CodeMarker events.
		# This and most providers are usually registered by default in Office16+.

		if (!$WPR_PreWin10) { EnsureETWProvider("..\OETW\MsoEtwCM.man") } # Relative to $ScriptRootPath

		# Ensure that any additional ETW Providers are registered.
		# These are usually of the form: ".\OETW\MsoEtwXX.man" or "..\OETW\MsoEtwXX.man"
		# The <provider ...> is the deepest tag required in the manifest.
		# https://learn.microsoft.com/en-us/windows/win32/wes/identifying-the-provider

		foreach ($ProviderManifest in $ProviderManifests)
		{
			EnsureETWProvider($ProviderManifest)
		}

		ClearProfileStartTime $InstanceName

		if (DoVerbose)
		{
			$Result = GetRunningTraceProviders $InstanceName
			if ($Result)
			{
				Write-Status "Active Trace Providers:"
				foreach ($Line in $Result) { Write-Status "`t$Line" }
			}
		}

		$TraceFilePath = GetTraceFilePathString $TraceName

		$WprParams += GetArgs -Stop $TraceFilePath -InstanceName $InstanceName

		# Don't bother with CLR PDB Generation if WPR supports the switch: -skipPdbGen
		# AND the trace doesn't contain the CLR providers, and -CLR wasn't specified.

		if (!$script:WPR_PreWin10 -and ($script:WPR_Win10Ver -ge '10.0.18955'))
		{
			if (!$CLR -and !(IsCLRTrace $InstanceName))
			{
				$WprParams += '-skipPdbGen'
			}
			else
			{
				Write-Status "CLR tracing is enabled. Ensuring generated CLR module symbols."
			}
		}

		if ($Boot) { $WPRParams = $WPRParams -replace '-Stop','-StopBoot' }

		$Result = InvokeWPR @WprParams

		switch ($LastExitCode)
		{

		0
		{
			if ($Boot -and $Result.Trim().EndsWith("Canceling the Autologger."))
			{
				Write-Warn $Result
				return [ResultValue]::Success # Canceled. No problem.
			}

			# The caller should write some result text or invoke:
			# WriteTraceCollected $TraceName

			return [ResultValue]::Collected
		}

		0xc5583000 # No Running Profiles
		{
			Write-Warn $Result

			if (TestRunningProfiles -CurrentScript)
			{
				Write-Warn "Run: $(GetScriptCommand) Stop [-Option]"
			}
			else
			{
				Write-Action "To view an existing trace, run: $(GetScriptCommand) View $ExtraParam"
			}

			return [ResultValue]::Success # Already stopped. No problem.
		}

		0x80010106 # Cannot change thread mode after it is set.
		{
			HandleChangeThreadMode

			Write-Msg "`nCanceling..."
			$Null = InvokeWPR -Cancel -InstanceName $InstanceName

			ListRunningProfiles

			return [ResultValue]::Error
		}

		0x800705aa # Insufficient system resources...
		{
			HandleInsufficientResources

			Write-Msg "`nCanceling..."
			$Null = InvokeWPR -Cancel -InstanceName $InstanceName

			ListRunningProfiles

			return [ResultValue]::Error
		}

		default
		{
			Write-Err $script:WPR_Path @WprParams

			if (!$Result) { $Result = "WPR returned this result:" }
			Write-Err $Result "`n`t" ('(0x{0:X8})' -f $LastExitCode)

			Write-Status "Canceling"
			$Null = InvokeWPR -Cancel -InstanceName $InstanceName

			ListRunningProfiles

			return [ResultValue]::Error
		}

		} # switch LastExitCode
		break
	} # Stop

	"Cancel"
	{
		Write-Msg "`nCanceling..."
		ClearProfileStartTime $InstanceName
		$WPRParams += GetArgs -Cancel -InstanceName $InstanceName
		if ($Boot) { $WPRParams = $WPRParams -replace '-Cancel','-CancelBoot' }
		$Result = InvokeWPR @WPRParams
		if (!$LastExitCode)
		{
			# Success
			Write-Warn "`nETW tracing has been canceled."
			ListRunningProfiles
		}
		else
		{
			# Failure
			Write-Warn $Result
			if (TestRunningProfiles -CurrentScript)
			{
				Write-Warn "Run: $(GetScriptCommand) Cancel [-Option]"
			}
		}
		break
	}

	"Status"
	{
		if ($Boot) { $InstanceName += '_boottr' } # added by WPR for the AutoLogger

		if (DoVerbose)
		{
			$Result = InvokeWPR -Status profiles collectors -InstanceName $InstanceName

			# Too much info: Remove filter IDs and trailing colons: "filtered in/out  by IDs:"
			# Replace "IDs:" with "IDs." and remove "<tab><tab><tab>...<eol>"
			$Result = $Result -replace ':\r','.'
			$Result = $Result -replace '\t\t\t.+\r\n',''
		}
		else
		{
			$Result = InvokeWPR -Status -InstanceName $InstanceName
		}

		Write-Msg $Result

		if (($LastExitCode -ne 0xC5583000) -and ($Result -notlike '* not recording*'))
		{
			# List any/all running traces.
			ListRunningProfiles

			if ("$RecordingProfiles".IndexOf("CPU") -ge 0)
			{
				# Tracing CPU, so report the profiling interval.
				if (!$WPR_PreWin10) { Write-Msg (InvokeWPR -ProfInt) }
			}

			if (!(DoVerbose))
			{
				Write-Action "For more information run: $(GetScriptCommand) Status $ExtraParam2 -Verbose"
			}
		}
		else
		{
			# Not Recording
			if (TestRunningProfiles -CurrentScript)
			{
				Write-Warn "Run: $(GetScriptCommand) Status [-Option]"
			}
		}

		break
	}

	default
	{
		Write-Status "Unknown Command: $Command"
		WriteUsage
		return [ResultValue]::Error
	}

	} # switch $Command

	return [ResultValue]::Success

} # ProcessTraceCommand


<#
	Setup and launch the Windows Performance Analyzer.
#>
function LaunchViewer
{
Param ( # $ViewerParams 'parameter splat'

	# Configuration.WpaProfile - multiple allowed
	[Parameter(Mandatory=$true)] [string[]]$ViewerConfigs,

	# Trace file name will be: $TraceName.ETL
	[Parameter(Mandatory=$true)] [string]$TraceName,

	# Optional: An alternate path to a trace file which overrides $TraceName.
	[string]$TraceFilePath,

	# Optional: Skip searching for PDB symbols. Load only cached/transcoded SymCache symbols.
	[switch]$FastSym,

	# Optional: Extra WPA parameters, or pseudo-param: -KeepRundown, -NoSymbols
	[string[]]$ExtraParams
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
		Write-Warn "This is the current location of the trace:"
		Write-Warn "`t$(ReplaceEnv $TraceFilePath)"
		Write-Warn "Please open it on another machine in this way:"
		Write-Warn "`t$(GetScriptCommand) View -Path <Trace_Path>\$(Split-Path -Leaf -Path $TraceFilePath)"
		exit 1
	}

	$Exe = "WPA.exe"

	# The viewer: Windows Performance Analyzer (WPA)
	$WpaPath = GetWptExePath $Exe
	$Version = GetFileVersion $WpaPath

	if ($Version)
	{
		# Pre-v10 WPA didn't accept -symbols param, nor HTTP symcache.
		$IsModernWPA = ($Version -ge [Version]'10.0.0')
	}
	else
	{
		# If we're launching WPA without a path, make this assumption:
		$IsModernWPA = IsModernScript
	}

	# If this isn't the modern WPA, launch the Pre-Win10 version of the script...unless we're already there.

	if ((!$IsModernWPA) -and (IsModernScript))
	{
		# Relaunch the Pre-Win10 script...if its folder exists.

		if (Test-Path -PathType container -Path $(ScriptPreWin10Path) -ErrorAction:SilentlyContinue)
		{
			$CmdPath = "$(ScriptPreWin10Path)\$($script:MyInvocation.MyCommand)"
			Write-Warn "Using an older version of the Windows Performance Analyzer (WPA)."
			Write-Warn "Running: $CmdPath" @PSScriptParams
			& $CmdPath @PSScriptParams >$Null
			exit $LastExitCode
		}

		Write-Err "Error: Cannot find this script folder: $(ScriptPreWin10Path)"
		Write-Err "Continuing with reduced functionality using this older version of WPA:`n" $WpaPath

		# Don't load modern .wpaProfile configuration profiles, which are almost surely incompatible.
		$ViewerConfigs = $Null
	}

	if ($Env:WPT_XPERF -or $Env:WPT_WPRP)
	{
		# If it looks like custom events were collected then include: CustomEvents.wpaProfile

		if ($Env:WPT_XPERF) { Write-Status "WPT_XPERF = $Env:WPT_XPERF" }
		if ($Env:WPT_WPRP)  { Write-Status "WPT_WPRP  = $Env:WPT_WPRP"  }

		$CustomProfile = "CustomEvents.wpaProfile"
		$CustomProfilePath = MakeFullPath "..\WPAP\$CustomProfile" # Relative to root of script-set
		if (Test-Path -PathType leaf -Path $CustomProfilePath -ErrorAction:SilentlyContinue)
		{
			# Put the Custom WPA Profile first, for the tab to appear on the left and not disturb UI & stacktags priority.
			$ViewerConfigs = [string[]]$CustomProfilePath + $ViewerConfigs
			Write-Status "Adding WPA Profile for custom traces: $CustomProfile"
		}
		else
		{
			Write-Status "Does not exist: $CustomProfilePath"
		}
	}

	# Now load LaunchViewerCommand and related.

	. "$ScriptRootPath\INCLUDE.WPA.ps1"

	Write-Msg "Opening trace:" (ReplaceEnv $TraceFilePath)

	$Process = LaunchViewerCommand $WpaPath $TraceFilePath $ViewerConfigs $Version -FastSym:$FastSym -ExtraParams:$ExtraParams

	# If no process launched (apparently), suggest a different path, or suggest reinstalling.

	if (!$Process)
	{
		# Get a different path.

		$Path2 = GetWptExePath $Exe -Silent -AltPath

		if ($Path2 -like "*\$Exe") # Has an alternate path?
		{
			Write-Warn "`nThe Windows Performance Analyzer ($Exe) apparently did not launch from:" (ReplaceEnv $WpaPath)
			Write-Warn "Solution: Please change the WPT_PATH environment variable to another path for WPA and try again:"
			$EnvPath = ReplaceEnv (Split-Path -Path $Path2)
			Write-Warn (Ternary (InvokedFromCMD) "`tset WPT_PATH=$EnvPath" "`t`$Env:WPT_PATH=$EnvPath")
		}
		else
		{
			Write-Warn "`nThe Windows Performance Analyzer ($Exe) apparently did not launch."
			Write-Warn "`nPerhaps reinstall WPA and try again.`n"
			if ($WpaPath -ne "WPA")
			{
				# Previously found <path>\WPA.*, but it did not launch.
				# Did not previously write the install message.
				WriteWPTInstallMessage $Exe
			}
		}
	}
	else
	{
		$Process.Close()
	}
} # LaunchViewer


<#
	This script works only with certain language modes.
#>
function CheckLanguageMode
{
	$LangAllow = 'FullLanguage', 'RestrictedLanguage'
	$LangBlock = 'ConstrainedLanguage', 'NoLanguage' # 'ConstrainedLanguage' appeared in PS v3.0

	$LanguageMode = $ExecutionContext.SessionState.LanguageMode

	switch ($LanguageMode)
	{
		{ $LangAllow -contains $_ }
		{
			# OK
			Write-Status "Language Mode: $_"
			break
		}
		{ $LangBlock -contains $_ }
		{
			Write-Warn "The current  Language Mode is: $_"
			Write-Warn "The required Language Mode is: $LangAllow"
			[string[]]$Config = $PSSessionConfigurationName -split '/'
			Write-Warn "The current session configuration is:" $Config[-1]
			Write-Warn "(Powershell 2.0 may not have the same restrictions.)"
			Write-Warn "See: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_language_modes"
			Write-Warn
			throw "Incompatible Language Mode" # should halt
			exit 1 # may not work!
			break
		}
		default
		{
			Write-Warn "Unrecognized language mode: $_"
			break
		}
	}
}


<#
	These actions must occur immediately upon load.
#>
# Main

CheckLanguageMode

# Native enum doesn't exist until PS v5.0
Add-Type -TypeDefinition @"
public enum ResultValue
{
	Success,
	Error,
	Started,
	Collected,
	View
}
"@
