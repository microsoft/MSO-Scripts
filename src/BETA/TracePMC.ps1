<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Capture and View an ETW trace of Hardware Performance Monitor Counters (PMC) / CPU Counters

	.DESCRIPTION

	.\TracePMC Start [-CPI] [-PMC Counter1[,Counter2,...]|*] [-Loop] [-CLR] [-JS]
	.\TracePMC Stop [-WPA [-FastSym]]
	.\TracePMC View [-Path <path>\MSO-Trace-PMC.etl|.wpapk] [-FastSym]
	.\TracePMC Status
	.\TracePMC Cancel
	  -CPI:  Capture Cycles per Instruction: TotalCycles & InstructionsRetired on each CSWITCH event
	  -PMC:  Sample the specified hardware counters, or * => CacheMisses,BranchMispredictions
	         Run: Generate-PmuRegFile   Run: WPR -PMCSources
	  -Loop: Record only the last few minutes of activity (circular memory buffer). 
	  -CLR:  Resolve call stacks for C# (Common Language Runtime).
	  -JS:   Resolve call stacks for JavaScript.
	  -WPA:  Launch the WPA viewer (Windows Performance Analyzer) with the collected trace.
	  -Path: Optional path to a previously collected trace.
	  -FastSym: Load symbols only from cached/transcoded SymCache, not from slower PDB files.
	            See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize
	  -Verbose

	.LINK

	https://github.com/microsoft/MSO-Scripts/wiki/Customize-Tracing#pmu
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-pmu-events
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows
	https://learn.microsoft.com/en-us/shows/defrag-tools/39-windows-performance-toolkit
#>

[CmdletBinding(DefaultParameterSetName = "View")]
Param(
	# "Start, Stop, Status, Cancel, View"
	[Parameter(Position=0)]
	[string]$Command,

	# Cycles per Instruction
	[Parameter(ParameterSetName="Start")]
	[switch]$CPI,

	# Hardware Performance Monitor Counters
	[Parameter(ParameterSetName="Start")]
	[string[]]$PMC,

	# Record only the last few minutes of activity (circular memory buffer).
	[Parameter(ParameterSetName="Start")]
	[switch]$Loop,

	# "Support Common Language Runtime / C#"
	[Parameter(ParameterSetName="Start")]
	[switch]$CLR,

	# "Support Common Language Runtime / C#"
	[Parameter(ParameterSetName="Start")]
	[switch]$JS,

	# "Launch WPA after collecting the trace"
	[Parameter(ParameterSetName="Stop")]
	[switch]$WPA,

	# "Optional path to a previously collected trace: MSO-Trace-PMC.etl"
	[Parameter(ParameterSetName="View")]
	[string]$Path = $Null,

	# "Faster symbol resolution by loading only from SymCache, not PDB"
	[Parameter(ParameterSetName="Stop")]
	[Parameter(ParameterSetName="View")]
	[switch]$FastSym

	# [switch]$Verbose # implicit
)

# ===== CUSTOMIZE THIS =====

	$WPRP_Events_Dft = ".\WPRP\CPUCounters.wprp!CPI"    # TotalCycles,InstructionsRetired
	$WPRP_Sample_Dft = ".\WPRP\CPUCounters.wprp!Misses" # CacheMisses,BranchMispredictions

	# These correspond to the <HardwareCounter> entries in .\WPRP\CPUCounters.wprp
	$PMC_Events_Dft = "TotalCycles","InstructionsRetired"  # Default Hardware Performance Monitor Counters for CPI Events on CSWITCH
	$PMC_Sample_Dft = "CacheMisses","BranchMispredictions" # Default Hardware Performance Monitor Counters for Sampling

	$TraceParams =
	@{
		RecordingProfiles =
		@(
			$WPRP_Events_Dft
			"..\WPRP\OfficeProviders.wprp!CodeMarkers"
			"..\WPRP\WindowsProviders.wprp!Basic"

		<#	^^^ The first entry is the base recording profile for this script.
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
			# Optional: Register ETW Provider Manifests not registered by default.
			# See: ..\OETW\ReadMe.txt
			"..\OETW\MsoEtwCM.man" # Office Code Markers
		)

		# This is the arbitrary name of the tracing session/instance:
		InstanceName = "MSO-Trace-PMC"
	}

	$ViewerParams =
	@{
		# The configuration files define the data tabs in the WPA viewer.
		# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
		ViewerConfig = "..\WPAP\BasicInfo.wpaProfile", ".\WPAP\CPUCounters.wpaProfile"

		# The default trace file name is: <InstanceName>.etl
		TraceName = $TraceParams.InstanceName

		# Optional alternate path to a previously collected ETL trace:
		TraceFilePath = $Path
	}

# ===== END CUSTOMIZE ====

if (!$script:PSScriptRoot) { $script:PSScriptRoot = Split-Path -Parent -Path $script:MyInvocation.MyCommand.Definition } # for PSv2
$script:ScriptHomePath = $PSScriptRoot
$script:ScriptRootPath = "$PSScriptRoot\.."
$script:PSScriptParams = $script:PSBoundParameters # volatile

. "$ScriptRootPath\INCLUDE.ps1"


<#
	Warn if Processor Monitor Counters are not available on this device.
#>
function FCheckPMC
{
	$Counters = $Null
	$TempFile = New-TemporaryFile

	try
	{
		Start-Process -FilePath 'WPR.exe' -ArgumentList '-PMCSources' -NoNewWindow -Wait -RedirectStandardOutput $TempFile -ErrorAction:Stop
		$Counters = Get-Content -LiteralPath $TempFile | Select-String -Pattern '\d' # 0-9
	}
	catch
	{
		$Counters = ""
	}

	Remove-Item $TempFile

	Write-Status "$($Counters.Count) Processor Monitor Counters listed."
	return ($Counters.Count -gt 2)
}


<#
	Transform an array of PMCs into a simple but well-formatted WPRP file, and return $Null or: <Path>!<ProfileName>
#>
function WPRPFromPMCList
{
Param (
	[string[]]$PMCs
)
	if (!$PMCs -or (!$PMCs.Count)) { return $Null }

	$_BUFFERS_      = 128 # MB # This buffer size should work well with all but the most verbose providers.
	$_INTERVAL_     = 4096 # Minimum collection interval between interrupts for most all counters. WPR -PMCSources
	$_PMCOUNTER_    = '_PMCOUNTER_' # replace
	$_WPRP_NAME_    = 'PMC_Profile.wprp'
	$_PROFILE_NAME_ = 'CustomSamples'
	$_DESCRIPTION_  = 'Profile for Hardware Performance Monitor Counters created by MSO-Scripts'

	$PMCs = $PMCs -replace '[<>]','_' # xml scrub: remove <brackets>

	$_Preamble =  "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n" +
	              "<!-- Automatically generated by MSO-Scripts via: $(Split-Path -Path $MyInvocation.ScriptName -Leaf)!$($MyInvocation.MyCommand.Name) -->`r`n" +
	              "<!-- From PMC list: $PMCs -->`r`n`r`n" +
	              "<WindowsPerformanceRecorder Version=`"1`" Author=`"MSO-Scripts`" >`r`n" +
	              "  <Profiles>`r`n`r`n"
	$_Collector = "    <SystemCollector Id=`"SC_Basic`" Name=`"MSO System Collector`">`r`n" +
	              "      <BufferSize Value=`"1024`" />`r`n" +
	              "      <Buffers Value=`"$_BUFFERS_`" />`r`n" +
	              "      <StackCaching BucketCount=`"256`" CacheSize=`"3072`" />`r`n" +
	              "    </SystemCollector>`r`n`r`n"
	$_Profile =   "    <Profile Name=`"$_PROFILE_NAME_`" Description=`"$_DESCRIPTION_`"`r`n" +
	              "     DetailLevel=`"Light`" LoggingMode=`"File`" Id=`"$_PROFILE_NAME_.Light.File`">`r`n" +
	              "      <Collectors Operation=`"Add`">`r`n" +
	              "        <SystemCollectorId Value=`"SC_Basic`">`r`n`r`n"
	$_SProvider = "          <SystemProvider Id=`"SP_SampleCounters`">`r`n" +
	              "            <Keywords Operation=`"Add`">`r`n" +
	              "              <Keyword Value=`"ProcessThread`" />`r`n" +
	              "              <Keyword Value=`"Loader`" />`r`n" +
	              "              <Keyword Value=`"PmcProfile`" />`r`n" +
	              "            </Keywords>`r`n" +
	              "            <Stacks Operation=`"Add`">`r`n" +
	              "              <Stack Value=`"PmcInterrupt`" />`r`n" +
	              "              <Stack Value=`"ImageLoad`" />`r`n" +
	              "            </Stacks>`r`n" +
	              "          </SystemProvider>`r`n`r`n"
	$_HCounter =  "          <HardwareCounter Id=`"HC_SampleCounters`" Strict=`"true`">`r`n" +
	              "            <SampledCounters>`r`n"
	$_SampledCtr ="              <SampledCounter Value=`"$_PMCOUNTER_`" Interval=`"$_INTERVAL_`" />`r`n"
	$_HCounterX = "            </SampledCounters>`r`n" +
	              "          </HardwareCounter>`r`n`r`n" +
	              "        </SystemCollectorId>`r`n"
	$_ProfileX =  "      </Collectors>`r`n" +
	              "    </Profile>`r`n`r`n"
	$_ProfMemory ="    <Profile Name=`"$_PROFILE_NAME_`" Description=`"$_DESCRIPTION_`"`r`n" +
	              "     DetailLevel=`"Light`" LoggingMode=`"Memory`" Base=`"$_PROFILE_NAME_.Light.File`" Id=`"$_PROFILE_NAME_.Light.Memory`" />`r`n`r`n"
	$_Postamble = "  </Profiles>`r`n" +
	              "</WindowsPerformanceRecorder>"

	$_WPRP = $_Preamble + $_Collector + $_Profile + $_SProvider + $_HCounter

	foreach ($PMC in $PMCs)
	{
		$_WPRP += $_SampledCtr -replace $_PMCOUNTER_,$PMC
	}

	$_WPRP += $_HCounterX + $_ProfileX + $_ProfMemory + $_Postamble

	$File = New-Item -Path $script:TracePath -Name $_WPRP_NAME_ -Type "file" -Force -Value $_WPRP -ErrorAction:SilentlyContinue -ErrorVariable:FileError

	if (!$File)
	{
		Write-Err $FileError
		return $Null
	}

	$ProfileOut = "$File!$_PROFILE_NAME_"

	Write-Status "Created temporary WPR Profile: $(ReplaceEnv $ProfileOut)"

	return $ProfileOut
}


# Main

	if ($Command -eq "Start")
	{
		$Win10VerMin = '10.0.18918' # Min version for both Windows and WPR for <EventNameFilters>
		$Win10VerCur = [Environment]::OSVersion.Version

		if ($Win10VerCur -lt $Win10VerMin)
        	{
			Write-Err "This trace requires Windows 10 v$Win10VerMin+. Current version is: v$Win10VerCur"
			exit 1
		}

		CheckPrerequisites # Sets script:WPR_Win10Ver

		if (!(!$script:WPR_PreWin10 -and ($script:WPR_Win10Ver -ge $Win10VerMin)))
        	{
			Write-Err "This trace requires WPR.exe v$Win10VerMin. Current version is: v$script:WPR_Win10Ver"
			Write-Err $script:WPR_Path
			exit 1
		}

		if (!(FCheckPMC))
		{
			Write-Msg
			Write-Warn "This device is not set up for tracing Process Monitor Counters." 
			Write-Warn "It is likely a Virtual Machine or a VM Host."
			Write-Warn "You may need to disable or uninstall HyperV features to access the PMC sources."
			Write-Warn "Expecting multiple counters listed: WPR.exe -PMCSources"
		}

		# 4 Modes:
		# 1. CPI (Cycles Per Instruction via CSWITCH) via CPUCounters.wprp!CPI
		# 2. Default Samples via CPUCounters.wprp!Misses
		# 3. Custom Samples via PMC_Profile.wprp!CustomSamples (dynamically generated)
		# 4. Both: CPI & Samples (Custom or Default) via CPUCounters.wprp!CPI & PMC_Profile.wprp!CustomSamples
		#    NOTE: To capture both modes of Hardware Performance Counters requires two WPRP files.

		[string]$WPRP_PMC = $Null

		if ($PMC)
		{
			# Cases 2,3,4

			if ($PMC -eq '*') # $PMC[] -contains '*'
			{
				# One way or another, there will be default samples: CacheMisses,BranchMispredictions

				$PMC = $PMC | ? { $_ -ne '*' }

				if ($PMC -or $CPI)
				{
					$PMC += $PMC_Sample_Dft # CacheMisses,BranchMispredictions
				}
			}

			$WPRP_PMC = WPRPFromPMCList $PMC
		}
		else # (!$PMC)
		{
			# Case 1
			# CPI mode is assumed.
			$CPI = $True
		}

		if ($WPRP_PMC)
		{
			# Append or insert the custom PMC sampling profile.
			if ($CPI) { $TraceParams.RecordingProfiles += $WPRP_PMC } # Case 4
			else { $TraceParams.RecordingProfiles[0] = $WPRP_PMC } # Case 3

			Write-Info "Capturing: $PMC" (Ternary $CPI "`nand CPI: $PMC_Events_Dft" $Null)
		}
		else
		{
			# Only use a profile in "CPUCounters.wprp"
			if (!$CPI) { $TraceParams.RecordingProfiles[0] = $WPRP_Sample_Dft } # Case 2
			# else { $TraceParams.RecordingProfiles[0] = $WPRP_Events_Dft } # Case 1 (default)

			Write-Info "Capturing" (Ternary $CPI $PMC_Events_Dft $PMC_Sample_Dft)
		}
	} # "Start"

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
	}

exit 0 # Success
