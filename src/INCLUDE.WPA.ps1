<#
	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	This file is specific to Windows Performance Analyzer (WPA)
	and symbol resolution.
#>

# if ($Host.Version.Major -gt 2) {Set-StrictMode -version latest }

<#
	The default SymbolDrive needs lots of free space for caching symbol files!
	You can reset symbol paths, with X: as the SymbolDrive, by doing this:
	PowerShell
		$Env:_NT_SYMBOL_PATH="cache*X:\symbols"
		$Env:_NT_SYMCACHE_PATH=$Null
		.\ResetWPA.ps1 -All
	CMD Prompt
		set _NT_SYMBOL_PATH=cache*X:\symbols
		set _NT_SYMCACHE_PATH=
		ResetWPA.bat -All
	X:\symbols must exist.
	Then run: Trace* View [-Path <Path_to_ETL>]

	Or you can change the default $SymbolDrive here.
#>
$script:SymbolDrive = "c:"

# Symbol Files (.pdb) are stored here by default. They can be HUGE!
$script:PdbCacheFolder = "$script:SymbolDrive\Symbols"

# SymCache Files (.symcache) are stored here by default.
$script:SymCacheFolder = "$script:SymbolDrive\SymCache"

$SymServPublic = "https://msdl.microsoft.com/download/symbols"

# These strings will eventually be -replaced with the actual cache folder paths.
$PDB_CACHE_FOLDER = "PDB_CACHE_FOLDER"
$SYM_CACHE_FOLDER = "SYM_CACHE_FOLDER"

# _NT_SYMBOL_PATH (External)
$script:PdbPathDefaultExternal = "cache*$PDB_CACHE_FOLDER;srv*$SymServPublic" # -replace

# _NT_SYMCACHE_PATH (External)
$script:SymCacheDefaultExternal = "$SYM_CACHE_FOLDER" # -replace


<#
	If the given parameter is in the parameter list, remove it and return $True.
#>
function HandlePseudoParam
{
Param (
	[ref]$Params,
	[string]$PseudoParam
)
	if ($Params.Value -contains $PseudoParam)
	{
		$Params.Value = $Params.Value | ? { $_ -ne $PseudoParam } # $Params.Value -= $PseudoParam # [ref]
		return $True
	}
	return $False
}


<#
	Set the default values for symbol-related environment variables.
	It may be better to set your own environment variables, with the cache folder on the largest drive.
	Or configure the symbol paths directly in WPA: Trace / Configure Symbol Paths
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/loading-symbols
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/load-symbols-or-configure-symbol-paths

	To set your own cache drive:folder and get the other symbol path defaults: set _NT_SYMBOL_PATH=cache*D:\SymCache
#>
function SetupSymbolPaths
{
Param (
	[bool]$SymCacheOnly
)
	$NT_SYMBOL_PATH = $env:_NT_SYMBOL_PATH
	$NT_SYMCACHE_PATH = $env:_NT_SYMCACHE_PATH

	# Get the first user-specified symbol (PDB) cache folder:
	#	cache*X:\symbols;...
	# OR	srv*X:\symbols*<server>;...
	# This folder will be used later as WPA's working directory.
	# This is important because downloading to a smaller drive can fill it up.

	$Paths = $NT_SYMBOL_PATH -split ";"
	$CacheFolder = $Paths -like "cache[*]?:\*" # cache*<X:folder>
	if (!$CacheFolder) { $CacheFolder = $Paths -like "srv[*]?:\*[*]*" } # srv*<X:cache_folder>*<server>
	if ($CacheFolder)
	{
		# _NT_SYMBOL_PATH contains: cache*X:\Path
		# OR: srv*X:\Path*<Server>
		# Use X:\Path as the cache folder, and later as the working directory for WPA.

		$CacheFolder = ($CacheFolder[0] -split "[*]")[1] # <cache_folder>

		mkdir $CacheFolder -ErrorAction:SilentlyContinue >$Null
		if (Test-Path -PathType container -Path $CacheFolder -ErrorAction:SilentlyContinue)
		{
			$script:SymbolDrive = ('{0}:' -f ($CacheFolder -split ":")[0])
			$script:PdbCacheFolder = $CacheFolder
			$script:SymCacheFolder = "$script:SymbolDrive\SymCache"
		}
		else
		{
			Write-Err "Does not exist: $CacheFolder"
			Write-Err "Resetting _NT_SYMBOL_PATH using: $script:PdbCacheFolder"

			$NT_SYMBOL_PATH = $Null
		}
	}

	# If the symbol path is only a cache*<folder> then give it the default path (which includes the cache folder).

	if ($NT_SYMBOL_PATH -eq "cache*$script:PdbCacheFolder")
	{
		Write-Status "Resetting _NT_SYMBOL_PATH using: $script:PdbCacheFolder"

		$NT_SYMBOL_PATH = $Null
	}

	$PdbPathDefault = $script:PdbPathDefaultExternal
	$SymCacheDefault = $script:SymCacheDefaultExternal

	if (!$NT_SYMBOL_PATH -and !$SymCacheOnly)
	{
		mkdir $script:PdbCacheFolder -ErrorAction:SilentlyContinue >$Null

		if (Test-Path -PathType container -Path $script:PdbCacheFolder -ErrorAction:SilentlyContinue)
		{
			$env:_NT_SYMBOL_PATH = $PdbPathDefault -replace $PDB_CACHE_FOLDER,$script:PdbCacheFolder

			Write-Warn "Setting _NT_SYMBOL_PATH = $env:_NT_SYMBOL_PATH"
			$DriveFreeSpace = GetDriveFreeSpace $script:SymbolDrive
			if ($DriveFreeSpace) { $DriveFreeSpace = "($([int]($DriveFreeSpace / 1GB)) GB Free)" }
			Write-Warn "NOTE: Cached symbols may consume lots of space on drive $script:SymbolDrive $DriveFreeSpace"
			Write-Warn
		}
		else
		{
			Write-Err "Cannot create: $script:PdbCacheFolder"
		}
	}

	if (!$NT_SYMCACHE_PATH)
	{
		mkdir $script:SymCacheFolder -ErrorAction:SilentlyContinue >$Null

		if (Test-Path -PathType container -Path $script:SymCacheFolder -ErrorAction:SilentlyContinue)
		{
			$env:_NT_SYMCACHE_PATH = $SymCacheDefault -replace $SYM_CACHE_FOLDER,$script:SymCacheFolder

			Write-Warn "Setting _NT_SYMCACHE_PATH = $env:_NT_SYMCACHE_PATH"
			Write-Warn
		}
		else
		{
			Write-Err "Cannot create: $script:SymCacheFolder"
		}
	}
} # SetupSymbolPaths


<#
	Get the path of the WPA configuration profile: "PATH\NAME.wpaProfile"
	If it does not exist, display an warning and return $Null.
#>
function GetWpaProfilePath
{
Param (
	[string]$WpaProfile
)
	$WpaProfile = MakeFullPath $WpaProfile
	$WpaProfilePath = Convert-Path -LiteralPath $WpaProfile -ErrorAction:SilentlyContinue

	if ($WpaProfilePath) { return $WpaProfilePath }

	Write-Warn "`nWarning: The WPA configuration profile does not exist:"
	Write-Warn $WpaProfile

	return $Null
}


<#
	The module Microsoft.PowerShell.Security needs to be explicitly loaded in some environments, with a default module path.
	https://learn.microsoft.com/en-us/powershell/scripting/whats-new/what-s-new-in-powershell-73#:~:text=Microsoft.PowerShell.Security
	https://github.com/PowerShell/PowerShell/issues/18530#issuecomment-1325691850
#>
function EnsureSecurity
{
Param (
	[string[]]$cmdlets
)
	if ($Host.Version.Major -lt 5) { return }

	$SecurityModule = 'Microsoft.PowerShell.Security'
	if ((Get-Module).Name -notcontains $SecurityModule)
	{
		Write-Status "Loading $SecurityModule for $cmdlets"
		$local:PSModulePath = $Env:PSModulePath
		$Env:PSModulePath = $Null # default path
		Import-Module -Name $SecurityModule -Cmdlet $cmdlets -Verbose:$False
		$Env:PSModulePath = $local:PSModulePath
	}
}


<#
	Determine whether all of the given file paths would be accessible from a process with Standard User privileges.
#>
function CheckFileUserPrivilege
{
	if (!$Args) { return $True } # PSv2

	# If any of these users have ReadData access, then it's accessible by a non-admin.
	$UserList = @("$Env:USERDOMAIN\$Env:USERNAME", "BUILTIN\Users", "NT AUTHORITY\Authenticated Users", "Everyone")
	$ReadData = [int][System.Security.AccessControl.FileSystemRights]::ReadData # 1

	EnsureSecurity 'Get-Acl'

	foreach ($Arg in $Args)
	{
		# Skip the switches and non-path args.
		if ($Arg -like "-*") { continue }
		$FilePath = $Arg.Trim('"')
		if (!(Test-Path -LiteralPath $FilePath -ErrorAction:SilentlyContinue)) { continue }

		$Readable = $False
		$Acl = Get-Acl $FilePath
		foreach ($Access in $Acl.Access)
		{
			if (!$Access) { break } # PSv2

			# BuiltIn\Users or Authenticated Users or Current User or Everyone
			if (($UserList -contains $Access.IdentityReference) -and ([int]$Access.FileSystemRights -band $ReadData))
			{
				if ($Access.AccessControlType -eq 'Deny') { break }
				$Readable = $True
				break
			}
		}
		if (!$Readable)
		{
			Write-Status "Not accessible as Standard User by ${Env:USERNAME}: $FilePath"
			return $False
		}
	}

	return $True
} # CheckFileUserPrivilege


<#
	Launch the given command with Standard, non-Admin permissions.
	Return an error string, or $Null.
#>
function LaunchAsStandardUser
{
	# Build the command string, replacing quotes with escaped quotes.

	$Command = $Null
	foreach ($Param in $Args) { $Command += "$($Param.Replace('`"','\`"')) " }

	# Some versions of WPA download the .PDB files to the working directory, independent of the symbol cache path.

	$WorkingDir = $script:TracePath
	if (Test-Path -PathType container -Path $script:PdbCacheFolder -ErrorAction:SilentlyContinue) { $WorkingDir = $script:PdbCacheFolder }
	Write-Status "Working Directory: $WorkingDir"

	# Some versions of Win11 before build 25247 require the /machine switch, else RunAs fails with 87: Invalid Parameter.
	[Version]$OSVer = [Environment]::OSVersion.Version
	if (($OSVer -ge [Version]'10.0.22000.0') -and ($OSVer -lt [Version]'10.0.25247.0'))
	{
		# StandardUser: 0x20000
		# Assuming WPA.exe is always x64/amd64 (for this range of Win11 builds).
		$ArgList = GetArgs /machine:amd64 /TrustLevel:0x20000 `"$Command`"
	}
	else
	{
		# StandardUser: 0x20000
		$ArgList = GetArgs /TrustLevel:0x20000 `"$Command`"
	}

	$ProcessCommand =
	@{
		FilePath = "RunAs.exe"
		ArgumentList = $ArgList
		WorkingDirectory = $WorkingDir
		NoNewWindow = $True # No new window for RunAs
		PassThru = $True
		Wait = $False # Would wait for child processes, too.
	}

	Write-Status "Launching as StandardUser (non-Admin):`n" $ProcessCommand.FilePath $ProcessCommand.ArgumentList

	$ErrorDefault = "Failed to run: $($Args[0])"
	$Error.Clear()

	try
	{
		$RunAsProcess = Start-Process @ProcessCommand
	}
	catch
	{
		Write-Status "Standard User:" $Error[0]
		if ($Error[0] -ne $Null) { return $Error[0] }
		return $ErrorDefault
	}

	# Wait for a result from RunAs.exe

	$HandleCache = $RunAsProcess.Handle # for PSv2 .ExitCode
	$Finished = $RunAsProcess.WaitForExit(20000)

	if ((!$Finished) -or ($RunAsProcess.ExitCode -ne 0))
	{
		Write-Status "As Standard User: $ErrorDefault"
		return $ErrorDefault
	}

	# The process launched, but we're not sure if it liked its arguments, configuration, etc.

	return $Null # No error
} # LaunchAsStandardUser


<#
	Launch the given command with the permissions of the current environment (possibly Admin).
	Return an error string, or $Null.
#>
function LaunchAsCurrentUser
{
Param(
	[string]$Viewer
)
	# Some versions of WPA download the .PDB files to the working directory, independent of the symbol cache path.

	$WorkingDir = $script:TracePath
	if (Test-Path -PathType container -Path $script:PdbCacheFolder -ErrorAction:SilentlyContinue) { $WorkingDir = $script:PdbCacheFolder }
	Write-Status "Working Directory: $WorkingDir"

	$ProcessCommand =
	@{
		FilePath = $Viewer
		ArgumentList = $Args # all other parameters
		WorkingDirectory = $WorkingDir
		WindowStyle = "Maximized"
		PassThru = $True
		Wait = $False
	}

	# If the viewer command is WPA.bat or something other than WPA.exe or "WPA.exe", don't maximize that window.
	if ($Viewer.TrimEnd('"') -notlike '*.exe') { $ProcessCommand.WindowStyle = "Minimized" }

	# WriteCmdVerbose $Viewer $Args # Done by the caller.

	$Error.Clear()
	try
	{
		$Process = Start-Process @ProcessCommand

		if ($Process.WaitForExit(2000) -and ($Process.ExitCode -lt 0)) { return "Early exit: " + ('{0:x}' -f $Process.ExitCode) }
	}
	catch
	{
		Write-Status "As Current User:" $Error[0]
		if ($Error[0] -ne $Null) { return $Error[0] }
		return "Failed to run: $($Args[0])"
	}

	# WPA.exe return values:
	# 0x00000000 - No Error
	# 0x00000001 - Didn't run, or killed by User
	# 0x80074005 - Canceled by User
	# 0xFFFFFFFF - User pressed 'Quit' to force WPA to close immediately

	return $Null # no error
} # LaunchAsCurrentUser


<#
	Launch a background process, xperf.exe (if it can be found on the machine),
	to download in the background symbols referenced by the ETW log file: $ETL
	See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#deeper
#>
function BackgroundResolveSymbols
{
Param (
	[string]$ETL
)
	$XPerfPath = GetWptExePath 'XPerf.exe' -silent

	if (!$XPerfPath)
	{
		Write-Status "Not downloading symbols in the background. Did not find: XPerf.exe"
		Write-Status 'Install the Windows Performance Toolkit from: https://aka.ms/adk'
		return $False
	}

	# Limit background symbol downloads to one window.

	$Downloading = Get-Process | Where-Object {$_.ProcessName -eq "xperf"}
	if ($Downloading)
	{
		Write-Status "Symbols are already downloading in another window."
		return $False
	}  

	$File = split-path -leaf -path $ETL

	if (!$Env:_NT_SYMBOL_PATH -or !$Env:_NT_SYMCACHE_PATH) { Write-Dbg '_NT_SYMBOL/SYMCACHE_PATH is not set. Caller runs: SetupSymbolPaths $False' }

	# Build the CMD commands to be run: CMD /c "<commands>"
	# https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#deeper

	$XPerfCmd = "`"$XPerfPath`" -tle -tti -i `"$ETL`" -symbols verbose -a symcache -build"

	$XPerfFiltered = "$XPerfCmd 2>&1 | findstr /r `"bytes.*SYMSRV.*RESULT..0x00000000`""

	$CmdHeader = "echo Downloading symbols for: $File & echo Symbol Path: %_NT_SYMBOL_PATH% & echo:" 

	$CmdNoTitle = "$CmdHeader & $XPerfFiltered" 

	$Title = "Download Symbols"

	$CmdCmd = "`"title $Title & $CmdNoTitle`""

	# LaunchAsStandardUser starts the process using RunAs, which always launches a non-minimized window.
	# Launch a new window (minimized and titled), and close the one from RunAs (after a brief flash).
	$RunAsCmd = "start `"$Title`" /min /belownormal cmd /c `"$CmdNoTitle`""

	WriteCmdVerbose $XPerfFiltered

	$ErrResult = $True
	[DateTime]$PreStartTime = Get-Date

	# Launch XPerf without Admin privileges, if possible.

	if ((CheckAdminPrivilege) -and (CheckFileUserPrivilege $XPerfPath $ETL))
	{
		# XPerf and the .ETL are acccessible as Standard User.
		$CmdArgs = GetArgs /c $RunAsCmd
		$ErrResult = LaunchAsStandardUser "cmd" @CmdArgs
	}

	if ($ErrResult)
	{
		# Launches a minimized CMD window.
		$CmdArgs = GetArgs /c $CmdCmd
		$ErrResult = LaunchAsCurrentUser "cmd" @CmdArgs
	}

	if (!$ErrResult)
	{
		# We're not convinced that XPerf is running, or perhaps it ended quickly.
		$Process = GetRunningProcess "xperf" $PreStartTime
		if ($Process) { return $True }

		Write-Err "Symbols may not have resolved in the background."
	}
	else
	{
		Write-Err (GetCmdVerbose $XPerfCmd)
		Write-Msg
		Write-Err "Not able to resolve symbols in the background."
		Write-Err $ErrResult
		Write-Msg
	}

	Write-Err "To retry, you can run:`n$script:ScriptRootPath\BETA\GetSymbols.bat $(ReplaceEnv $ETL)"

	return $False
} # BackgroundResolveSymbols


<#
	Launch WPA on the given path using the given parameters.
	Return the process object on success, else warn and return $Null.
#>
function LaunchViewerCommand
{
Param (
	[string]$ViewerPath,
	[string]$TraceFilePath,
	[string[]]$ViewerConfigs,
	[Version]$VersionInfo,
	[switch]$FastSym,
	# Optional WPA parameters, or pseudo-param: -KeepRundown, -NoSymbols
	[string[]]$ExtraParams
)
	$KeepRundown = HandlePseudoParam ([ref]$ExtraParams) '-KeepRundown'
	$NoSymbols = HandlePseudoParam ([ref]$ExtraParams) '-NoSymbols'

	# Pre-v10 WPA didn't accept -symbols param, nor HTTP symcache.
	$IsModernWPA = ($VersionInfo -ge [Version]'10.0.0')

	# Should have previously called EnsureTracePath
	if (!$script:TracePath) { EnsureTracePath; Write-Dbg "Trace path not set for default working folder!" }

	$SymCacheOnly = $False

	[array]$ViewerCmd = GetArgs -i `"$TraceFilePath`"
	if (!$ExtraParams)
	{
		if ($IsModernWPA)
		{
			# If WPA option -cliprundown needed, it must immediately follow -i file.
			if (!$KeepRundown)
			{
				$ViewerCmd += GetArgs -cliprundown
			}

			if (!$NoSymbols)
			{
				$ViewerCmd += GetArgs -symbols

				if ($FastSym)
				{
					# Not-well-documented -symcacheonly switch loads symbols only from .symcache files, ignoring PDBs.
					# (Ideally we wouldn't need a special switch for fast symbol resolution, but the benefit is dramatic.)
					# Run: wpa.exe -help "Event Tracing for Windows"
					# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/loading-symbols#symcache-path
					# https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize

					$ViewerCmd += GetArgs -symcacheonly
					$SymCacheOnly = $True
				}
			}
		}
	}
	else
	{
		# if $ExtraParams are -Processor & -Addsearchdir then they come before -Profile.
		# And other switches like -Symbols, -SymCacheOnly and -ClipRundown don't seem to work.
		$ViewerCmd += $ExtraParams

		if ($FastSym)
		{
			if ($Env:_NT_SYMBOL_PATH) { Write-Dbg "Not taking action on -FastSym, yet _NT_SYMBOL_PATH is set." }
			$SymCacheOnly = $True
		}
	}

	SetupSymbolPaths $SymCacheOnly

	# Add the viewer configuration file(s): *.wpaProfile
	# But only for .ETL, not .WPAPK

	if ($TraceFilePath -notlike "*.wpapk")
	{
		foreach ($ViewerConfig in $ViewerConfigs)
		{
			if (!$ViewerConfig) { break } # PSv2

			$WpaProfilePath = GetWpaProfilePath $ViewerConfig

			if (!$WpaProfilePath) { continue }

			if ($VersionInfo -and $IsModernWPA)
			{
				# Some of the .wpaProfile configuration files are versioned.  Find the best one.

				$ProfileVersionName = GetVersionedFileName $WpaProfilePath "wpaProfile" $VersionInfo
				if ($ProfileVersionName)
				{
					Write-Status "Using $ProfileVersionName with WPA v$VersionInfo"
					$WpaProfilePath = $WpaProfilePath -replace (Split-Path -Leaf -Path $WpaProfilePath),$ProfileVersionName
				}
			}

			$ViewerCmd += GetArgs -profile `"$WpaProfilePath`"
		}
	}

	Write-Msg "Launching Windows Performance Analyzer (WPA) ..."
	WriteCmdVerbose $ViewerPath $ViewerCmd

	[DateTime]$PreStartTime = Get-Date

	# Launch the viewer without Admin privileges, if possible.

	$Process = $Null
	$ErrResult = $True
	if ((CheckAdminPrivilege) -and (CheckFileUserPrivilege $ViewerPath @ViewerCmd))
	{
		# Admin, but all file paths are acccessible as Standard User.
		$ErrResult = LaunchAsStandardUser $ViewerPath @ViewerCmd

		if (!$ErrResult)
		{
			# We're not convinced that WPA actually launched.

			$Process = GetRunningProcess "WPA" $PreStartTime

			if (!$Process)
			{
				Write-Status "WPA apparently did not launch as StandardUser (non-Admin)."
				Write-Status "Retrying as Current User (Admin)."
				$ErrResult = $True
			}
		}
	}
	if ($ErrResult)
	{
		$ErrResult = LaunchAsCurrentUser $ViewerPath @ViewerCmd
	}
	if ($ErrResult)
	{
		Write-Err (GetCmdVerbose $ViewerPath $ViewerCmd)
		Write-Msg
		Write-Err "Windows Performance Analyzer did not launch."
		Write-Err $ErrResult
		if (!(DoVerbose))
		{
			Write-Msg
			Write-Err "To retry, please run: $(GetScriptCommand) View -Verbose [options]"
		}
		return $Null
	}

	# Get the launched WPA process. Give it a priority boost. Then return the process.

	if (!$Process) { $Process = GetRunningProcess "WPA" $PreStartTime }

	if ($Process)
	{
		$Process.PriorityClass = 'AboveNormal'

		if (!$IsModernWPA)
		{
			# Warn if the -symbols switch was not used. (Pre-Win10 versions of WPA didn't accept it.)
			Write-Warn "`nTo resolve call stack symbols in WPA, select: Trace / Load Symbols"
		}
		elseif ($FastSym)
		{
			# Launch XPerf in the background to download referenced symbols not already cached.

			SetupSymbolPaths $False # NOW set up _NT_SYMBOL_PATH

			Write-Warn "FastSym: WPA is loading only symbols previously cached or transcoded to SymCache."

			$fResolving = BackgroundResolveSymbols $TraceFilePath
			if ($fResolving) { Write-Warn "Referenced symbols are being downloaded in the background." }

			Write-Warn "https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#fastsym"
		}
	}

	return $Process
} # LaunchViewerCommand
