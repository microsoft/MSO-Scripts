# if ($Host.Version.Major -gt 2) { Set-StrictMode -version latest }

<#
	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	In older versions of Office, a certain type of Diagnostic Logging was enabled via cryptic registry values found here:
		HKCU:\SOFTWARE\Microsoft\Office\16.0\Common\ExperimentEcs\<AppName>\Overrides  # See $OfficeAppList
	Writing diagnostic log files to:
		$Env:TEMP\Diagnostics\<ProcessName>\

	In more recent versions of Office, this Diagnostic Logging is enabled by default, and the registry values control the max logging size.
	See:	https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/collect-office-diagnostic-logs
		https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/diagnostic-log-collection-in-support-and-recovery-assistant

	In fact, it is possible to disable Office Diagnostic Logging by default via this registry key!value :
		HKCU:\SOFTWARE\Microsoft\Office\16.0\Common\ExperimentEcs\All\Overrides!ofzh3kkunwubxh0 = "false" [REG_SZ]

	This script controls Office Diagnostic Logging per application via registry:
		HKCU:\SOFTWARE\Microsoft\Office\16.0\Common\ExperimentEcs\<AppName>\Overrides!ofzh3kkunwubxh0 = "true" [REG_SZ]

	Also, the Current User registry paths may be different between the Office app (Logged-on User?) and this running script (Administrator?).
	Therefore the script will duplicate these HKCU:\ entries for both versions of the registry path (if different).

	See: SetLoggedOnRegistry_Office and EnableLogging_Office

	Other logging mechanisms also exist, which can be captured via the $ExtraLogs customizable logging definitions.

	See: EnableExtraLogging

	cf.	https://learn.microsoft.com/en-us/office/troubleshoot/diagnostic-logs/how-to-enable-office-365-proplus-uls-logging
		https://learn.microsoft.com/en-us/office/troubleshoot/installation/office-setup-issues
		https://learn.microsoft.com/en-us/archive/msdn-technet-forums/f22ab205-3dda-4d94-ab8c-3e04b82a12ab
		https://techcommunity.microsoft.com/t5/microsoft-365-apps-for/enable-trust-center-logging-tcdiag/m-p/3881359
	Also:	https://support.microsoft.com/en-us/office/diagnostic-data-in-microsoft-365-f409137d-15d3-4803-a8ae-d26fcbfc91dd
		https://support.microsoft.com/en-us/office/overview-of-diagnostic-log-files-for-office-fba86aac-70dc-4858-ae1f-ec2034346cdf
		https://learn.microsoft.com/en-US/outlook/troubleshoot/performance/how-to-scan-outlook-by-using-microsoft-support-and-recovery-assistant

#>

# Prereq: $script:OfficeAppList
# There is also a Registry Key for 'All', alongside the app name keys.
$script:OfficeAppListAll = ,'All' + $script:OfficeAppList


<#
	Return the value of the script-scope variable of the given name (no leading '$'), else $Null.
#>
function GetVariable
{
Param (
	[string]$VarName
)
	return Get-Variable -Name $VarName -Scope Script -ValueOnly -ErrorAction:SilentlyContinue
}


<#
	Re/Set the registry value.
	Re/Store any previous value.
	Auto-chooses the registry value type: [string]->REG_SZ, [int]->REG_DWORD, etc.
	If $fEnable then the result will be in $Error[0]
#>
function SetTempRegValue
{
Param (
	[bool]$fEnable,
	[string]$Key,
	[string]$Value,
	$Data
)
	$ValueOrig = "$Value.Orig" # the value overwritten
	$DataOrig = GetRegistryValue $Key $ValueOrig
	[int]$DataGone = 0x600DF00D # arbitrary positive sentinel

	if ($fEnable)
	{
		if ($DataOrig -eq $Null)
		{
			# Get and store the original value data.
			$DataOrig = GetRegistryValue $Key $Value
			if ($DataOrig) { SetRegistryValue $Key $ValueOrig $Null $DataOrig }
			else { SetRegistryValue $Key $ValueOrig "DWORD" $DataGone }
		}
		if ($DataOrig -ne $Data)
		{
			SetRegistryValue $Key $Value $Null $Data 
		}
	}
	else # Reset
	{
		if ($DataOrig -ne $Null)
		{
			# Clear or restore the original value / type.
			ClearRegistryValue $Key $Value
			if ($DataOrig -ne $DataGone) { SetRegistryValue $Key $Value $Null $DataOrig }
			ClearRegistryValue $Key $ValueOrig
		}
		ResetError
	}
}


<#
	Get the registry prefix for the currently logged-in user if it is different from the context of this script.
	$Null or Registry::HKEY_USERS\<LoggedOnSID>\
#>
function _GetUserRegKeyPrefix
{
	$KeyPrefix = $Null

	# Get the current logged-on User SID, or $Null if not different.

	$LoggedOnSID = GetLoggedOnUserSID

	if ($LoggedOnSID) # Not $Null or $False
	{
		$KeyPrefix2 = "Registry::HKEY_USERS\$LoggedOnSID\"

		# Sanity check this registry path of the logged-on user.

		if (Test-Path -PathType Container -LiteralPath $KeyPrefix2 -ErrorAction:SilentlyContinue)
		{
			Write-Status "Enabling registry access to: HKEY_USERS\$LoggedOnSID"
			$KeyPrefix = $KeyPrefix2
		}
	}

	return $KeyPrefix
}


<#
	Get the registry prefix for the currently logged-in user:
	HKCU:\
	Registry::HKEY_USERS\<LoggedOnSID>\
#>
function GetUserRegKeyPrefix
{
	$KeyPrefix = _GetUserRegKeyPrefix

	if (!$KeyPrefix) { $KeyPrefix = 'HKCU:\' }

	return $KeyPrefix
}


<#
	Return $True if the trace folder is: $Env:LocalAppDataAlt\MSO-Scripts\Office\<Date-Time>
#>
function HaveTraceLogFolder { return $script:TracePath -NotLike "*\MSO-Scripts" }

	
<#
	Copy all files newer than $StartTime from:
		$Logs[]
	To:
		$LogPath\<Date-Time>\...
	Returns:
		Collected: Logs were successfully copied to the new folder: $script:TracePath (changed)
		Started: Logging started, but no logs were collected.
		View: Collection was not started. (View previous log files.)
		Error: Logs were not successfully copied.
	NOTE: Fails when any log path is a folder.
#>
function DoGatherLogs
{
Param (
	[DateTime]$StartTime,
	[string]$LogPath,
	[string[]]$Logs
)
	if (!$StartTime)
	{
		Write-Warn "Collection of logs was not started, or it already stopped."
		Write-Warn "Previous log files may be stored here:" (ReplaceEnv $LogPath)
		return [ResultValue]::View
	}

	Write-Status "Gathering logs collected since: $StartTime"

	if (!$Logs) { return [ResultValue]::Started }

	if (DoVerbose)
	{
		# Confirm that there are no empty log entries or folders.
		$NotEmpty = $Logs | Where-Object { ![string]::IsNullOrEmpty($_) }
		if ($NotEmpty.Count -ne $Logs.Count) { Write-Dbg "Null entries in log file list!" }

		$Folders = $NotEmpty | Where-Object { (Get-Item $_).Attributes -band [io.fileattributes]::Directory }
		if ($Folders) { Write-Dbg "Folders in the file list:`n$Folders" }
	}

	# Copy the files to a common folder.

	# yyyy-mm-ddThh.mm.ss
	$Now = Get-Date -UFormat "%Y-%m-%dT%H.%M.%S"

	$LogPathNow = "$LogPath\$Now"

	if (EnsureWritableFolder $LogPathNow)
	{
		Write-Status "Original Office logs are here:`n$($Logs | Out-String)"
		Write-Status "Copying logs to: $LogPathNow"

		Copy-Item -LiteralPath $Logs -Destination $LogPathNow -Container:$False -Force -ErrorAction:SilentlyContinue

		# Confirm that all of the files copied to the new, dated folder.

		$CountDest = Get-ChildItem -Path "$LogPathNow\*" -ErrorAction:SilentlyContinue | Measure-Object
		if ($CountDest -and ($CountDest.Count -ge $Logs.Count))
		{
			Write-Status "Copied $($CountDest.Count) files."

			# Add the alternate path for future logs/traces (via WPR) to this same folder.

			$script:TracePath = $LogPathNow
			Write-Status "Changing trace collecton folder to:`n$($script:TracePath)"

			return [ResultValue]::Collected
		}
	}

	Write-Err "Could not copy all files!"
	if (!(DoVerbose)) { Write-Err "Rerun with -verbose" }
	
	[ResultValue]::Error
}


<#
	Compress the log/trace files into a .zip file adjacent to the gathered log/trace folder.
	Do nothing if the earlier GatherLogs somehow failed.
	Return the path\name of the generated .zip file, or $Null.
#>
function ZipTraceFolder
{
	if ($Host.Version.Major -lt 5) { return $Null } # PSv5+

	# EnsureTracePath had set  $script:TracePath = LOCALAPPDATA\MSO-Scripts
	# If GatherLogs succeeded, $script:TracePath = LOCALAPPDATA\MSO-Scripts\Office\DateTime

	if (!(HaveTraceLogFolder)) { return $Null } # Unchanged from EnsureTracePath

	$ZipCount = Get-Item -Path "$script:TracePath\*" -ErrorAction:SilentlyContinue | Measure-Object

	if ((!$ZipCount) -or ($ZipCount.Count -le 1)) { return $Null }

	$ZipPath = "$script:TracePath.zip"

	Write-Status "Zipping $ZipCount log/trace files to: $ZipPath"

	ResetError

	try
	{
		# Verbose: Compress-Archive lists all archived files.
		Compress-Archive -Path "$script:TracePath\*" -DestinationPath $ZipPath -CompressionLevel:Optimal -Force -ErrorAction:Stop
	}
	catch
	{
		Write-Status "Could not create a ZIP!"
		Write-Status $Error[0]
		return $Null
	}

	return $ZipPath
}


function WriteTraceCollected_Office
{
Param (
	[string]$InstanceName
)
	if (_WriteTraceCollected $InstanceName)
	{
		if (IsNetworkTrace $InstanceName)
		{
			# Make it clear that network providers are included in the trace.

			$TraceFilePath = GetTraceFilePathString $InstanceName
			Write-Msg "And:    TraceNetwork View -Path $TraceFilePath"
		}
	}
}


<#
	Archive the log and trace files into a single .ZIP if possible.
	Report the results as appropriate.
#>
function ArchiveGatheredLogs
{
Param (
	[string]$InstanceName,
	[switch]$Trace = $True,
	[switch]$Shh
)
		if (!$Shh -and $Trace)
		{
			WriteTraceCollected_Office $InstanceName
		}

		$ZipPath = ZipTraceFolder
		if ($ZipPath)
		{
			Write-Msg "`nFinal logs/traces are archived here: $(GetEnvPath $ZipPath)`n$ZipPath`n"
		}
		elseif (HaveTraceLogFolder)
		{
			Write-Msg "`nFinal logs/traces are stored here: $(GetEnvPath $script:TracePath)`n$($script:TracePath)`n"
		}
		elseif ($Shh -and $Trace)
		{
			# Either there is only this MSO-Trace-NAME.etl, or the logs are scattered about.
			# If we didn't say this earlier, now is the time.

			WriteTraceCollected_Office $InstanceName
		}
}


<#
	When ETW/ETL traces are stashed in time-stamped sub-folders, return the most recent one.
	Or in certain error cases, the trace may be stored in the main folder: $script:TracePath
	This might return a file path which doesn't exist (where the trace file should be).
#>
function GetRecentTraceFilePath
{
Param (
	[string]$SubPath, # Office, Outlook, ...
	[string]$TraceName
)
	EnsureTracePath

	$TraceFilePath = "$script:TracePath\$SubPath"
	if (!(Test-Path -PathType Container -LiteralPath $TraceFilePath -ErrorAction:SilentlyContinue)) { return $Null }

	$TraceFilePath = Get-ChildItem -Path $TraceFilePath -Attributes Directory -Recurse
	if (!$TraceFilePath) { return $Null }

	$TraceFilePath = $TraceFilePath | Sort-Object -Descending -Property LastWriteTime | Select-Object -First 1
	$TraceFilePath = "$($TraceFilePath.FullName)\$TraceName.etl"
	$TraceFileBase = "$script:TracePath\$TraceName.etl"

	if (Test-Path -PathType Leaf -LiteralPath $TraceFileBase -ErrorAction:SilentlyContinue)
	{
		# Does the trace file in the sub-folder even exist? (If not, DoGatherLogs must have failed to update: $script:TracePath)
		if (!(Test-Path -PathType Leaf -LiteralPath $TraceFilePath -ErrorAction:SilentlyContinue))
		{
			return $TraceFileBase
		}

		# Is the trace file in the base folder newer!? (If so, DoGatherLogs must have failed to update: $script:TracePath)
		if ((Get-Item -LiteralPath $TraceFileBase).CreationTime -gt (Get-Item -LiteralPath $TraceFilePath).CreationTime)
		{
			return $TraceFileBase
		}
	}

	return $TraceFilePath
}


<#
	Write the location of the most recent log/trace folder or zip.
#>
function WriteRecentLogPath
{
Param (
	[string]$GroupName
)
	EnsureTracePath
	$TracePath = Get-ChildItem -Path "$script:TracePath\$GroupName\*" -ErrorAction:SilentlyContinue | Sort-Object -Descending -Property LastWriteTime | Select-Object -First 1
	if ($TracePath)
	{
		Write-Msg "The most recent traces are here: $(GetEnvPath $TracePath)`n$($TracePath)"
	}
}


<#
	Return processes currently running, based on the given list.  Wait for them to quit (and warn).
	Ignore processes which started before the given start time, if provided (when tracing is stopping).
#>
function CheckProcessListState
{
Param (
	[string[]]$ProcessList,
	[Nullable[DateTime]]$StartTime
)
	# Get array of running processes, most recent first.
	$Processes = Get-Process $ProcessList -ErrorAction:SilentlyContinue | Sort-Object -Descending -Property StartTime
	if (!$Processes) { return $Null }

	[array]$ProcessNames = $Null

	foreach ($Process in $Processes)
	{
		if ($StartTime -and ($Process.StartTime -lt $StartTime)) { continue }

		# Office apps may still be quitting.  Wait 5 seconds.

		if (!$ProcessNames)
		{
			Write-Status "Waiting for process $($Process.ProcessName) to exit. PID =" $Process.id

			$Process.WaitForExit(5000) > $Null
		}

		if (!$Process.HasExited)
		{
			Write-Warn "`tThe process $($Process.ProcessName) is still running."

			if (!$Process.Responding) { Write-Action "It is not responding and might need to be killed. PID =" $Process.id }

			Write-Status "Running:" $Process.MainModule.ModuleName "PID =" $Process.id "v$($Process.MainModule.FileVersionInfo.FileVersion)" "Running since:" $Process.StartTime

			$ProcessNames += $Process.ProcessName
		}
	}

	return $ProcessNames
}


<#
	Create the logging registry key paths and values.
	Also create the paths of the logged-on user for Office, if different from that of this script.
#>
function SetLoggedOnRegistry_Office
{
	if (GetVariable "RegOfficeOptionsWord") { return } # for strict mode

	$KeyPrefix = _GetUserRegKeyPrefix

	# $script:RegOfficeOptionsXXXX = "HKCU:\SOFTWARE\Microsoft\Office\16.0\Common\ExperimentEcs\XXXX\Overrides"

	foreach ($App in $script:OfficeAppListAll)
	{
		New-Variable -Name RegOfficeOptions$App -Scope Script -Value "HKCU:\Software\Microsoft\Office\16.0\Common\ExperimentEcs\$App\Overrides"

		if ($KeyPrefix)
		{
			# The Office App(s) MAY be running in a different (non-Administrator) context.
			New-Variable -Name RegOfficeOptions$($App)Alt -Scope Script -Value "$($KeyPrefix)Software\Microsoft\Office\16.0\Common\ExperimentEcs\$App\Overrides"
		}
	}

	$script:RegValTraceCollectionToFileKey = "ofzh3kkunwubxh0"
	$script:RegValFileBasedKey =             "of5vnrlxp1dlgl0"
	$script:RegValMaxSizeKey =               "of1xdahnnfitxk1"
	$script:RegValFileSizeKey =              "ofk41bz2kboxnm1"
	$script:RegValFilesNumberKey =           "ofvazksbo6jvr41"
}


<#
	Set/Reset Office Diagnostic Logging via registry
	The logs are at: $Env:Temp\Diagnostics\<ProcessName>\*.log  and  $Env:Temp\Diagnostics\<ProcessName>\Additional\*.log
	cf. OfficeDiagnosticLogging.bat
	https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/diagnostic-logs/collect-office-diagnostic-logs
#>
function EnableLogging_Office
{
Param (
	[bool]$fEnable
)
	# Create the logging registry paths to the current and signed-in user.
	SetLoggedOnRegistry_Office

	if ($fEnable) { Write-Status "Setting Diagnostic Logging for future Office processes:" $script:OfficeAppList }
	else { Write-Status "Resetting Diagnostic Logging for future Office processes." }

	[int]$FileCount = 2 * $script:MaxSize / $script:FileSize

	# For Office 2016+ / 365
	foreach ($App in $script:OfficeAppList)
	{
		$RegOfficeOptionsApp = GetVariable "RegOfficeOptions$App"

		SetTempRegValue $fEnable $RegOfficeOptionsApp $script:RegValTraceCollectionToFileKey "true"
		SetTempRegValue $fEnable $RegOfficeOptionsApp $script:RegValFileBasedKey "true"
		SetTempRegValue $fEnable $RegOfficeOptionsApp $script:RegValMaxSizeKey $script:MaxSize
		SetTempRegValue $fEnable $RegOfficeOptionsApp $script:RegValFileSizeKey $script:FileSize
		SetTempRegValue $fEnable $RegOfficeOptionsApp $script:RegValFilesNumberKey $FileCount

		# The Office App(s) might be running within a different context than the current script (Administrator).

		$RegOfficeOptionsAppAlt = GetVariable "RegOfficeOptions$($App)Alt"

		if ($RegOfficeOptionsAppAlt)
		{
			SetTempRegValue $fEnable $RegOfficeOptionsAppAlt $script:RegValTraceCollectionToFileKey "true"
			SetTempRegValue $fEnable $RegOfficeOptionsAppAlt $script:RegValFileBasedKey "true"
			SetTempRegValue $fEnable $RegOfficeOptionsAppAlt $script:RegValMaxSizeKey $script:MaxSize
			SetTempRegValue $fEnable $RegOfficeOptionsAppAlt $script:RegValFileSizeKey $script:FileSize
			SetTempRegValue $fEnable $RegOfficeOptionsAppAlt $script:RegValFilesNumberKey $FileCount
		}
	}
}


<#
	Transform an 'unresolved path' with embedded $variables
	to a resolved, alternate path which refers to the logged-on user, if needed, else $Null.
#>
function ResolveAltPath
{
Param (
	[string]$Path
)
	if (!$Env:TempAlt) { Write-Dbg "Must invoke SetLoggedOnUserEnv" }

	# See SetLoggedOnUserEnv
	$UserProfile =  [regex]::Escape('$Env:UserProfile\')
	$UserProfileAlt =               '$Env:UserProfileAlt\'
	$LocalAppData = [regex]::Escape('$Env:LocalAppData\')
	$LocalAppDataAlt =              '$Env:LocalAppDataAlt\'
	$Temp =         [regex]::Escape('$Env:Temp\')
	$TempAlt =                      '$Env:TempAlt\'

	# Replace embedded $variables with the alternate versions.

	$AltPath = $Path -ireplace $UserProfile,$UserProfileAlt
	$AltPath = $AltPath -ireplace $LocalAppData,$LocalAppDataAlt
	$AltPath = $AltPath -ireplace $Temp,$TempAlt

	if ($AltPath -eq $Path) { return $Null } # no replacements

	return $ExecutionContext.InvokeCommand.ExpandString($AltPath) # resolve any embedded $variables
}


<#
	Set/reset the array of (logging) registry values in the format: 'HKCU:\KeyPath!ValueName=Value'
	Embedded $trace:Variables and $Env:Variables will be resolved herein.
	If the target process(es) _might_ be running in a different user context, duplicate the HKCU registry paths.
#>
function EnableExtraLogging
{
Param (
	[bool]$fEnable,
	# Customizable array of: 'HKCU:\KeyPath!ValueName=Value'
	[string[]]$KeyValues,
	# Customizable array of: 'LogFolderPath'
	[string[]]$NewFolders
)
	if (!$script:TracePath) { EnsureTracePath; Write-Dbg "Trace path not previously initialized." }

	if ($fEnable)
	{
		# Ensure folders required for logging.

		foreach ($NewFolder in $NewFolders)
		{
			$Folder = $ExecutionContext.InvokeCommand.ExpandString($NewFolder)
			$Null = New-Item -Path $Folder -ItemType "directory" -ErrorAction:SilentlyContinue

			$Folder = ResolveAltPath $NewFolder
			if ($Folder) { $Null = New-Item -Path $Folder -ItemType "directory" -ErrorAction:SilentlyContinue }
		}
	}

	# If the Office App(s) might be running in a different context then get the alternate registry path prefix.
	$KeyPrefixAlt = _GetUserRegKeyPrefix

 	if ($fEnable -and $KeyValues) { Write-Status "Enabling extra logging for future Office processes." }
	else { Write-Status "Resetting extra logging for future Office processes." }

	# Enable/disable the registry values required for logging.

	foreach ($KeyValue in $KeyValues)
	{
		$KeyValue = $ExecutionContext.InvokeCommand.ExpandString($KeyValue) # resolve embedded $variables

		# Separate: KeyPath ! ValueName = Value
		[string[]]$Parts = $KeyValue -split '!|='

		if ($Parts.length -ne 3)
		{
			Write-Dbg "Invalid registry format: $KeyValue`nExpected: KeyPath!ValueName=Value"
			continue
		}

		$KeyPathAlt = $Null
		$KeyValueAlt = $Null

		if ($KeyPrefixAlt -and ($Parts[0].StartsWith('HKCU:\', 'OrdinalIgnoreCase')))
		{
			$KeyPathAlt = $Parts[0] -replace [regex]::Escape('HKCU:\'),$KeyPrefixAlt
		}

		if ($fEnable)
		{
			Write-Status $KeyValue

			if ($KeyPathAlt)
			{
				$KeyValueAlt = "$KeyPathAlt!$($Parts[1])=$($Parts[2])"
				Write-Status $KeyValueAlt
			}
		}

		$IntPart = $Parts[2] -as [int]
		if ($IntPart)
		{
			SetTempRegValue $fEnable $Parts[0] $Parts[1] $IntPart
		}
		else
		{
			SetTempRegValue $fEnable $Parts[0] $Parts[1] $Parts[2]
		}

		if ($fEnable -and $Error)
		{
			Write-Dbg $KeyValue
			Write-Dbg "Registry Error: $Error"
		}

		if (!$KeyPathAlt) { continue }

		if ($IntPart)
		{
			SetTempRegValue $fEnable $KeyPathAlt $Parts[1] $IntPart
		}
		else
		{
			SetTempRegValue $fEnable $KeyPathAlt $Parts[1] $Parts[2]
		}

		if ($fEnable -and $Error)
		{
			Write-Dbg $KeyValueAlt
			Write-Dbg "Registry Error: $Error"
		}
	}
}


<#
	Helper for WriteLoggingStatus*
#>
function WriteLoggingValue
{
Param (
	[string]$Path,
	[string]$Value
)
	$Result = GetRegistryValue $Path $Value
	if (!$Result) { return } # Ignore 0 or $Null
	Write-Dbg "$Path!$Value = $Result"
}


<#
	If -Verbose then write out any relevant, non-zero registry value.
#>
function WriteLoggingStatus_Office
{
	if (!(DoVerbose)) { return }

	SetLoggedOnRegistry_Office

	# For Office 2016+ / 365
	foreach ($App in $script:OfficeAppListAll)
	{
		$RegOfficeOptionsApp = GetVariable "RegOfficeOptions$App"

		WriteLoggingValue $RegOfficeOptionsApp $script:RegValTraceCollectionToFileKey
		WriteLoggingValue $RegOfficeOptionsApp $script:RegValFileBasedKey
		WriteLoggingValue $RegOfficeOptionsApp $script:RegValMaxSizeKey
		WriteLoggingValue $RegOfficeOptionsApp $script:RegValFileSizeKey
		WriteLoggingValue $RegOfficeOptionsApp $script:RegValFilesNumberKey

		# The Office App(s) might be running within a different context than the current script (Administrator).

		$RegOfficeOptionsAppAlt = GetVariable "RegOfficeOptions$($App)Alt"

		if ($RegOfficeOptionsAppAlt)
		{
			WriteLoggingValue $RegOfficeOptionsAppAlt $script:RegValTraceCollectionToFileKey
			WriteLoggingValue $RegOfficeOptionsAppAlt $script:RegValFileBasedKey
			WriteLoggingValue $RegOfficeOptionsAppAlt $script:RegValMaxSizeKey
			WriteLoggingValue $RegOfficeOptionsAppAlt $script:RegValFileSizeKey
			WriteLoggingValue $RegOfficeOptionsAppAlt $script:RegValFilesNumberKey
		}
	}
}


<#
	If -Verbose then write out any relevant, non-zero registry values based on the input array of: RegPath!ValueName
#>
function WriteExtraLoggingStatus
{
Param (
	# Customizable array of: "HKCU:\KeyPath!ValueName" or "HKCU:\KeyPath!ValueName=Value" (ignore "=Value")
	[string[]]$KeyValues
)
	if (!(DoVerbose)) { return }

	if (!$script:TracePath) { EnsureTracePath; Write-Dbg "Trace path not previously initialized." }

	# If the Office App(s) might be running in a different context then get the alternate registry path prefix.
	$KeyPrefixAlt = _GetUserRegKeyPrefix

	foreach ($KeyValue in $KeyValues)
	{
		$KeyValue = $ExecutionContext.InvokeCommand.ExpandString($KeyValue) # resolve embedded $variables

		# Separate KeyPath, ValueName, Value
		[string[]]$Parts = $KeyValue -split '!|='

		if ($Parts.length -lt 2) { continue }

		WriteLoggingValue $Parts[0] $Parts[1]

		if ($KeyPrefixAlt -and ($Parts[0].StartsWith('HKCU:\', 'OrdinalIgnoreCase')))
		{
			$KeyPathAlt = $Parts[0] -replace [regex]::Escape('HKCU:\'),$KeyPrefixAlt

			WriteLoggingValue $KeyPathAlt $Parts[1]
		}
	}
}


<#
	Determine if this tracing session is collecting CLR info.
#>
function IsNetworkTrace
{
Param (
	[string]$InstanceName
)
	$Result = GetRunningTraceProviders $InstanceName

	return ($Result -like "*Winsock-AFD*")
}
