<#
	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	Tracing Heap activity requires a fair bit of extra preparation.
#>

# if ($Host.Version.Major -gt 2) { Set-StrictMode -version latest }

# This value in Heap.wprp gets replaced with the target ProcessID.
$HeapDummyPID = "12345678"

# WPR sets this registry value to enable [expensive] heap tracing for MyApp.exe: $HeapTracingKey\MyApp.exe!TracingFlags=1
$HeapTracingKey = "HKLM:\Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
$HeapTracingName = "TracingProcess"
$HeapSnapshotName = "SnapshotProcess"
$HeapSnapshotPID = "SnapshotProcID"

function CompareStringArray
{
Param (
	[string[]]$rgStr1,
	[string[]]$rgStr2
)
	if (!$rgStr1 -or !$rgStr2) { return $False }
	return -not (Compare-Object $rgStr1 $rgStr2)
}


<#
	$True if the Enter key was recently pressed.
#>
function TestEnterKey
{
	if ($Host.UI.RawUI.KeyAvailable)
	{
		$Key = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
		if ($Key -and ($Key.Character -eq "`r")) { return $True }
	}
	return $False
}


function Write-Processes
{
Param (
	[System.Diagnostics.Process[]]$Processes
)
	Write-Msg
	Write-Msg "Tracked processes:"
	foreach ($Process in $Processes)
	{
		Write-Msg "`t$($Process.ProcessName) [$($Process.ID)]"
	}
}


<#
	Set the given process name to start tracing heap allocations the next time that it launches.
	Or not. Or show the current status.
#>
function SetHeapTracingConfig
{
Param (
	[string]$ProcessName,
	[switch]$Enable,
	[switch]$Disable
)
	if (!$WPR_PreWin10)
	{
		if ($Disable) { return InvokeWPR -HeapTracingConfig $ProcessName disable }
		if ($Enable) { return InvokeWPR -HeapTracingConfig $ProcessName enable }
		return InvokeWPR -HeapTracingConfig $ProcessName
	}

	# The Pre-Win10 version of WPR does not have the -HeapTracingConfig option.

	if (!$Enable -and !$Disable)
	{
		$TracingFlags = GetRegistryValue "$HeapTracingKey\$ProcessName" "TracingFlags"
		if ((!$TracingFlags) -or -not ($TracingFlags -band 1))
		{ return "`n`tHeap tracing is disabled for the process: $ProcessName`n" }
		else
		{ return "`n`tHeap tracing is enabled for the process: $ProcessName`n" }
	}

	SetRegistryValue "$HeapTracingKey\$ProcessName" "TracingFlags" "DWORD" ([int][bool]$Enable)
	if (!$Error[0])
	{
		if ($Enable)
		{ return "`n`tHeap tracing was successfull enabled for the process: $ProcessName`n" }
		else
		{ return "`n`tHeap tracing was successfull disabled for the process: $ProcessName`n" }
	}

	return $Error[0]
} # SetHeapTracingConfig

<#
	Set the given process name to start capturing heap allocations for snapshot the next time that it launches.
	Or not. Or show the current status.
#>
function SetHeapSnapshotConfig
{
Param (
	[string]$ProcessName,
	[switch]$Enable,
	[switch]$Disable
)
	if ($Disable) { return InvokeWPR -SnapshotConfig Heap -Name $ProcessName disable }
	if ($Enable) { return InvokeWPR -SnapshotConfig Heap -Name $ProcessName enable }
	return InvokeWPR -SnapshotConfig Heap -Name $ProcessName
}


<#
	Enable/Disable heap tracing/snapshots for a particular future process when it launches.
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-for-heap-analysis
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/record-heap-snapshot
	TODO: Modern/Store Apps: $ProcessName [<package full name> <package relative app ID>]
#>
function SetHeapProcessName
{
Param (
	[string]$HeapValueName,
	[string]$SetHeapConfigFunc,
	# ProcessName.exe (Reset if $Null)
	[string[]]$SetNames
)
	$ResetNames = GetRegistryValue $HeapTracingKey $HeapValueName

	# Are we about to set the heap tracing/snapshot process name list to what it already is?
	if (CompareStringArray $SetNames $ResetNames)
	{
		Write-Warn "Heap capture is already enabled for: $SetNames"
		return
	}

	# Disable heap tracing/snapshot for future processes which have the previously given name.

	if ($ResetNames)
	{
		foreach ($ResetName in $ResetNames)
		{
			Write-Msg (& $SetHeapConfigFunc $ResetName -Disable)
		}

		ClearRegistryValue $HeapTracingKey $HeapValueName
	}

	# Enable heap tracing/snapshot for future processes with the given name.

	if ($SetNames)
	{
		foreach ($SetName in $SetNames)
		{
			Write-Msg (& $SetHeapConfigFunc $SetName -Enable)
		}

		# Remember the name of the process(es) to be traced so that they can be disabled later.
		Set-ItemProperty -Path $HeapTracingKey -Name $HeapValueName -Type MultiString -Value $SetNames >$Null
	}
} # SetHeapProcessName

function SetHeapTracingProcessName
{
Param (
	# ProcessName.exe (Reset if $Null)
	[string[]]$SetNames
)
	SetHeapProcessName $HeapTracingName 'SetHeapTracingConfig' $SetNames
}

function SetHeapSnapshotProcessName
{
Param (
	# ProcessName.exe (Reset if $Null)
	[string[]]$SetNames
)
	SetHeapProcessName $HeapSnapshotName 'SetHeapSnapshotConfig' $SetNames
}


<#
	What process is set for Heap tracing when it launches?
#>
function ShowHeapProcessName
{
Param ( [string]$HeapValueName,
	[string]$SetHeapConfigFunc
)
	# Show the heap tracing status for future processes which have the previously given ProcessName.exe

	[string[]]$ProcessNames = GetRegistryValue $HeapTracingKey $HeapValueName

	if ($ProcessNames)
	{
		foreach ($ProcessName in $ProcessNames)
		{
			Write-Msg (& $SetHeapConfigFunc $ProcessName)
		}
	}
	else
	{
		Write-Status "Heap capture by process name is not enabled."
	}
} # ShowHeapProcessName

function ShowHeapTracingProcessName
{
	ShowHeapProcessName $HeapTracingName 'SetHeapTracingConfig'
}

function ShowHeapSnapshotProcessName
{
	ShowHeapProcessName $HeapSnapshotName 'SetHeapSnapshotConfig'
}


<#
	Invoke WPR and write a warning of the output if needed.
	Usage: InvokeWprAndWarn <Commands> -InstanceName $InstanceName
#>
function InvokeWprAndWarn
{
	$Result = InvokeWpr @Args

	# Ignore: E_WPRC_RUNTIME_STATE_PROFILES_NOT_RUNNING
	if (($Result -and $LastExitCode -and ($LastExitCode -ne 0xc5583000)) -or (DoVerbose))
	{
		# Stringify any parameter array.
		# Don't bother to write the InstanceName params.
		[string[]]$Params = $Args[0..($Args.Count-3)] | ? { $_ }
		Write-Warn "'WPR $Params' returned: 0x$('{0:x}' -f $LastExitCode)`n$Result"
	}
}


<#
	Return $False if any string doesn't look like valid: NAME.exe
	Warn if any string represents a process already running.
#>
function TestExeList
{
Param (
	[string[]]$ExeList
)
	foreach ($EXE in $ExeList)
	{
		if (!($EXE -like "*.exe"))
		{
			Write-Err "Unexpected process name: $EXE"
			Write-Err "The process to trace should have the extension: .exe"
			return $False
		}
	}

	$ProcList = Get-Process -Name ($ExeList -replace '\.exe$') -ErrorAction:SilentlyContinue
	if ($ProcList)
	{
		Write-Warn "Warning: To capture a heap trace of a running process, specify the Process ID rather than the name."
		Write-Warn "These process(es) are already running, and will not be traced:"
		foreach ($Proc in $ProcList)
		{
			Write-Warn "`t$($Proc.ProcessName).exe ($($Proc.id)) Running since:" $Proc.StartTime
		}
	}

	return $True
}


<#
	WPR asks for names of processes to track, then asks for their PIDs to capture snapshots.

	Return an array of unique Process IDs
	which represents our best guess as to which processes can give us Heap Snapshots.
	Note that there could be 8+ PIDs in the array (which is WPRs limit per command).
#>
function GetSnapshotProcessIDs
{
Param (
	[string]$InstanceName,
	[Parameter(Mandatory=$False)][int]$ProcessID,
	[switch]$Reset
)
	[int[]]$PIDs = $Null
	[System.Diagnostics.Process[]]$Processes = $Null

	# Get a list of running processes, filtered by name and start time.

	$Names = GetRegistryValue $HeapTracingKey $HeapSnapshotName
	$Names = $Names -replace '.exe$' # Strip trailing .exe

	if ($Names)
	{
		$Processes = Get-Process -Name $Names -ErrorAction:SilentlyContinue

		if ($Processes)
		{
			# If process names were set in the IFEO (Registry), then OS Restart doesn't reset that.
			$CaptureStartTime = GetProfileStartDateTime $InstanceName -XSession

			if ($CaptureStartTime)
			{
				Write-Status "Tracking processes named '$Names', launched since: $CaptureStartTime"

				$Processes = $Processes | where StartTime -ge $CaptureStartTime
			}
			else
			{
				Write-Warn "No Heap Capture start time available!"
			}

			if ($Processes) { Write-Processes $Processes }
		}

		if ($Reset)
		{
			SetHeapSnapshotProcessName # Reset
		}
	} # $Names

	# Add a list of explicit PIDs, converted from 'MultiString'

	try
	{
		# Auto-coerce to int[]
		$PIDs = GetRegistryValue $HeapTracingKey $HeapSnapshotPID
	}
	catch
	{
		# Failed to coerce to int[]
		Write-Err "Failed to retrieve PIDs from the registry:"
		Write-Err $(GetRegistryValue $HeapTracingKey $HeapSnapshotPID)
		if ($Reset) { ClearRegistryValue $HeapTracingKey $HeapSnapshotPID }
	}

	if ($Reset -and $PIDs)
	{
		ClearRegistryValue $HeapTracingKey $HeapSnapshotPID
	}

	# Add the parameter PID.

	if ($ProcessID)
	{
		$PIDs += $ProcessID
	}

	# Merge the explicit PIDs with those of the running processes.

	if ($PIDs)
	{
		Write-Status "Tracking explicit PIDs: $PIDs"

		if ($Processes)
		{
			$PIDs += $Processes.ID
		}

		# Filter to running processes, and remove duplicate PIDs.

		$Processes = Get-Process -Id $PIDs -ErrorAction:SilentlyContinue
		if ($Processes) { Write-Processes $Processes }
	}

	if (!$Processes)
	{
		Write-Warn "No currently running processes are actively tracking Windows Heap for Snapshots."
		return $Null
	}

	return $Processes.ID
}


<#
	Disable heap snapshots with the previously given PID and/or ProcessName.exe
	Optionally do one last snapshot now.
#>
function DisableSnapshots
{
Param (
	[int]$ProcessID,
	[string]$InstanceName,
	[switch]$Snap
)
	InvokeWprAndWarn -DisablePeriodicSnapshot Heap -InstanceName $InstanceName

	[int[]]$ProcessIDs = GetSnapshotProcessIDs $InstanceName $ProcessID -Reset

	if ($ProcessIDs)
	{
		# Limit 8 PIDs at a time
		for ([int]$i = 0; $i -lt $ProcessIDs.Count; $i = $i + 8)
		{
			[int[]]$PIDs = $ProcessIDs[$i..($i+7)] # Up to the next 8 Process IDs

			# Capture one final snapshot.

			if ($Snap)
			{
				InvokeWprAndWarn -SingleSnapshot Heap $PIDs -InstanceName $InstanceName

				# Timeout - try again
				if ($LastExitCode -eq 0x800705b4)
				{
					Write-Warn "Retrying..."
					InvokeWprAndWarn -SingleSnapshot Heap $PIDs -InstanceName $InstanceName
				}
			}

			# Stop tracking heap activity in these specific processes.

			InvokeWprAndWarn -SnapshotConfig Heap -PID $PIDs Disable -InstanceName $InstanceName
		}
	}
}


<#
	Do the extra setup required to enable heap tracing:
	either a running process ($ProcessID) or a process to be launched ($EXE), or both.
	On "Start" success, sets: [ref]$EXEs = "ProcessName.exe"
#>
function PrepareHeapTraceCommand
{
Param (
	[string]$Command,
	[HashTable]$TraceParams, # mutable
	[int]$ProcessID, # can be $Null in PSv2
	[ref]$EXEs
)
	if ($Command -eq "View") { return [ResultValue]::View }

	CheckPrerequisites

	switch ($Command)
	{

	"Start"
	{
		[string[]]$ExeList = $EXEs.Value

		if ($ProcessID)
		{
			# Enable heap tracing for a running process by ProcessID.

			$ProcessName = $Null
			$Process = Get-Process -Id $ProcessID -ErrorAction:SilentlyContinue
			if ($Process) { $ProcessName = $Process.ProcessName }

			if (!$ProcessName)
			{
				Write-Err "There is no process $ProcessID currently running."
				return [ResultValue]::Error
			}

			$EXEs.Value = $Process.MainModule.ModuleName # [ref]

			# Strangely, the dynamic ProcessID must be stored in the static Heap.wprp file.
			# Rewrite Heap.wprp with the target ProcessID.

			$WprProfile = ValidateRecordingProfileString $TraceParams.RecordingProfiles[0]

			if (!$WprProfile)
			{
				Write-Err "Error: The base heap recording profile is not valid:" $TraceParams.RecordingProfiles[0]
				return [ResultValue]::Error
			}

			$ProfileName = GetProfileName $WprProfile
			$WprFile = StripProfileName $WprProfile

			if (!$ProfileName)
			{
				if (!$WPR_PreWin10)
				{ Write-Err "Warning: Tracing heap with a ProcessID requires a recording profile with a name: $WprFile!<Name>" }
				else
				{ Write-Err "Warning: Tracing heap with a ProcessID requires a recording profile with .Light/.Verbose: $WprFile.Light" }

				return [ResultValue]::Error
			}

			# Check for the dummy value of HeapProcessId within the file.
			if (Select-String -Path $WprFile -Pattern $HeapDummyPID)
			{
				Write-Msg "Enabling heap tracing for: $ProcessName [$ProcessID]"

				# Rewrite the .wprp file to the TracePath folder with the target PID embedded.

				$WprFileNew = "$script:TracePath\Heap_$ProcessID.wprp"
				[string]$Content = Get-Content -LiteralPath $WprFile
				$Content.Replace($HeapDummyPID,[string]$ProcessID) | Set-Content -Force -Path $WprFileNew
				$TraceParams.RecordingProfiles[0] = CreateProfileName $WprFileNew $ProfileName

				Write-Status "Generated heap recording profile for '$ProcessName' [$ProcessID]: $($TraceParams.RecordingProfiles[0])"
			}
			else
			{
				Write-Warn "Warning: Assuming the 'HeapProcessId' [$ProcessID] is embedded in the profile: $(CreateProfileName $WprFile $ProfileName)"
				Write-Warn "https://learn.microsoft.com/en-us/windows-hardware/test/wpt/heapprocessid"
			}
		}
		elseif (!$ExeList)
		{
			WriteUsage
			exit 1
		}

		if ($ExeList)
		{
			if (!(TestExeList $ExeList)) { return [ResultValue]::Error }

			# Enable heap tracing of future processes which have this Name.exe
			SetHeapTracingProcessName -SetName $ExeList
		}
		break
	}

	"Stop"
	{
		# Disable heap tracing of future processes with the previously given Name.exe
		SetHeapTracingProcessName # Reset
		Remove-Item "$script:TracePath\Heap*.wprp" -ErrorAction:SilentlyContinue >$Null
		break
	}

	"Cancel"
	{
		# Disable heap tracing of future processes with the previously given Name.exe
		SetHeapTracingProcessName # Reset
		Remove-Item "$script:TracePath\Heap_*.wprp" -ErrorAction:SilentlyContinue >$Null
		break
	}

	"Status"
	{
		# Show the status of heap tracing of future processes with the previously given Name.exe
		ShowHeapTracingProcessName
		break
	}

	} # switch Command

	return [ResultValue]::Success
} # PrepareHeapTraceCommand


<#
	Do the extra setup required to enable heap snapshot capture:
	either a running process ($ProcessID) or a process to be launched ($EXE), or both.
	On "Start" success, sets: [ref]$EXEs = "ProcessName.exe"
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/record-heap-snapshot
#>
function PrepareHeapSnapshotCommand
{
Param (
	[string]$Command,
	[int]$ProcessID,
	[ref]$EXEs,
	[string]$InstanceName
)
	if ($Command -eq "View") { return [ResultValue]::View }

	# Check Windows Version

	$VerMin = [Version]'10.0.17112.0' # Win10-RS4 - Min Windows Version for Heap Snapshots
	$Version = [Environment]::OSVersion.Version

	if ($Version -lt $VerMin)
	{
		if ($Command -eq "Cancel") { return [ResultValue]::Error }

		Write-Err "Capturing Heap Snapshots requires a more recent version of Windows 10+."
		Write-Err "Current Windows Version: $Version  Required Minimum Version: $VerMin"
		return [ResultValue]::Error
	}

	CheckPrerequisites

	# Check WPR Version

	$VerMin = [Version]'10.0.16194' # Min WPR Version for Heap Snapshots
	$Version = $script:WPR_Win10Ver

	if (!$Version -or ($Version -lt $VerMin))
	{
		if ($Command -eq "Cancel") { return [ResultValue]::Error }

		Write-Err "Requires Windows 10 and WPR v$VerMin"
		Write-Err "Currently running v$Version at: $($script:WPR_Path)"
		Write-Err "You can set the environment variable WPT_PATH to the path of a newer WPR.exe ."
		return [ResultValue]::Error
	}

	switch ($Command)
	{

	"Start"
	{
		[string[]]$ExeList = $EXEs.Value

		if ($ProcessID) # not 0, not $null
		{
			# Enable heap snapshots for a running process by ProcessID.

			$Process = Get-Process -Id $ProcessID -ErrorAction:SilentlyContinue

			if (!$Process)
			{
				Write-Err "There is no process $ProcessID currently running."
				return [ResultValue]::Error
			}

			$EXEs.Value = $Process.MainModule.ModuleName # [ref]

			Write-Msg "Enabling heap tracing for: $($Process.ProcessName) [$($Process.ID)]"

			# Currently a single PID (limit 8 at a time)
			$Result = InvokeWpr -SnapshotConfig Heap -PID $Process.ID Enable -InstanceName $InstanceName

			if (!$LastExitCode) # no error
			{
				Write-Msg $Result # "Heap snapshot was enabled for pid ..."

				# Remember the PIDs of the Snapshot-enabled process(es). See: GetSnapshotProcessIDs
				SetRegistryValue $HeapTracingKey $HeapSnapshotPID 'MultiString' $Process.ID
			}
			else
			{
				Write-Err "'WPR -SnapshotConfig Heap -PID $($Process.ID) Enable -InstanceName $InstanceName' returned:"
				Write-Err $Result
				return [ResultValue]::Error
			}
		}
		elseif (!$ExeList)
		{
			WriteUsage
			exit 1
		}

		if ($ExeList)
		{
			if (!(TestExeList $ExeList)) { return [ResultValue]::Error }

			# Enable heap tracing of future processes which have this/these Name.exe

			SetHeapSnapshotProcessName -SetName $ExeList
		}
		break
	}

	"Stop"
	{
		# Disable heap snapshots with the previously given PID and/or Name.exe
		# Do one last snapshot now.

		DisableSnapshots $ProcessID $InstanceName -Snap
		break
	}

	"Cancel"
	{
		# Disable heap snapshots with the previously given PID and/or Name.exe

		DisableSnapshots $ProcessID $InstanceName
		break
	}

	"Status"
	{
		# Show heap capture enabled by process name.

		ShowHeapSnapshotProcessName

		# Show heap capture enabled in currently running processes.

		$Null = GetSnapshotProcessIDs $InstanceName

		break
	}

	} # switch Command

	return [ResultValue]::Success
} # PrepareHeapSnapshotCommand


<#
	Wait for the given EXE(s) to launch, then set the snapshot characteristics (periodic interval).
	Returns [ResultValue]::
		Started: Processes currently running.
		Success: Waiting for processes to run.
		Error:   Failed or ambiguous.
#>
function PostProcessSnapshotCommand
{
Param (
	[string]$Command,
	[ref]$ProcessID,
	[string[]]$EXEs,
	# Seconds between snapshots for WPR -EnablePeriodicSnapshot
	[int]$SnapshotInterval,
	[string]$InstanceName
)
	if ($Command -ne "Start") { return [ResultValue]::Error }

	[int]$PID = [int]$ProcessID.Value

	if ($PID)
	{
		$Process = Get-Process -Id $PID -ErrorAction:SilentlyContinue
		if (!$Process)
		{
			Write-Status "There is no process PID = $PID"
			return [ResultValue]::Error
		}

		Write-Msg "Capturing a snapshot of outstanding Windows Heap allocations every $SnapshotInterval seconds."
		$Result = InvokeWpr -EnablePeriodicSnapshot Heap $SnapshotInterval $Process.ID -InstanceName $InstanceName
		if ($Result) { Write-Warn "'WPR -EnablePeriodicSnapshot Heap $SnapshotInterval $($Process.ID) -InstanceName $InstanceName' returned:`n$Result" }
		Write-Msg

		return [ResultValue]::Started
	}

	if (!$EXEs -or !$EXEs.Count -or [string]::IsNullOrEmpty($EXEs[0])) { return [ResultValue]::Error }

	[System.Diagnostics.Process[]]$Processes = $Null

	$Names = $EXEs -replace '.exe$' # Strip trailing .exe

	# If process names were set in the IFEO (Registry), then OS Restart doesn't reset that.
	$CaptureStartTime = GetProfileStartDateTime $InstanceName -XSession

	Write-Msg "Waiting for a process to launch:" $EXEs
	Write-Msg "To skip this step, press Enter."

	while ($True)
	{
		$Processes = Get-Process -Name $Names -ErrorAction:SilentlyContinue

		if ($Processes)
		{
			if ($CaptureStartTime)
			{
				$Processes = $Processes | where StartTime -ge $CaptureStartTime
			}

			if ($Processes)
			{
				Write-Processes $Processes
				break
			}
		}

		if (TestEnterKey) { break }

		Start-Sleep 1
	}

	Write-Msg

	if ($Processes)
	{
		Write-Msg "Capturing a snapshot of outstanding Windows Heap allocations for $($Processes.ProcessName | Get-Unique -AsString) [$($Processes.Id)] every $SnapshotInterval seconds."
		$Result = InvokeWpr -EnablePeriodicSnapshot Heap $SnapshotInterval $Processes.ID -InstanceName $InstanceName
		if ($Result) { Write-Status "'WPR -EnablePeriodicSnapshot Heap $SnapshotInterval $($Processes.Id) -InstanceName $InstanceName' returned:`n$Result" }
		Write-Msg

		$ProcessID.Value = $Processes[0].Id # [ref]

		$Return = [ResultValue]::Started

		if ($Names.Count -le 1) { return $Return }

		# Here we've waited for just one of multiple target Apps that are enabled for Heap Snapshot capture.
		# That's the best we can do for now.
		# Tell the user how to collect periodic or one-shot Heap Snapshots for other Apps/Processes if they want.
		# In any case, they'll get a single Heap Snapshot at the end of tracing with the Stop command.
	}
	else
	{
		Write-Msg "A snapshot of outstanding Windows Heap allocations will be captured when tracing stops."
		Write-Msg

		$Return = [ResultValue]::Success

		# Here the user opted to not wait for the target App(s) to launch.
		# Tell them how to collect periodic or one-shot Heap Snapshots for other Apps/Processes if they want.
		# In any case, they'll get a single Heap Snapshot at the end of tracing with the Stop command.
	}

	Write-Msg "You can manually capture individual Heap snapshots by Process ID by running:"
	Write-Msg "`tWPR -SingleSnapshot Heap <PIDs> -InstanceName $InstanceName"
	Write-Msg "Or set a Heap snapshot time interval by running:"
	Write-Msg "`tWPR -EnablePeriodicSnapshot Heap <Interval_in_Sec> <PIDs> -InstanceName $InstanceName"
	Write-Msg "Where WPR is at:" (ReplaceEnv $script:WPR_Path)
	Write-Msg

	return $Return
} # PostProcessSnapshotCommand
