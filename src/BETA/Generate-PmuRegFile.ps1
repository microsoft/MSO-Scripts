<#
    .NOTES

    Copyright (c) Microsoft Corporation.
    Licensed under the MIT License.

    .SYNOPSIS

    Creates a reg file for all Intel Microarchitectural PMU Sources for a given
    family and model. This reg file is usable by the PMC Extensibility Framework
    to use Microarchitectural PMU sources with ETW.

    .DESCRIPTION

    Generate-PmuRegFile [-Family <f> -Model <m>] [-OutputDirectory <dir>] [-InputFile <file>] [-Description] [-Keep]
      -Family: Supplies the Family of the CPU to get PMU Sources for.
               Default: Family of the current CPU
      -Model:  Supplies the Model of the CPU to get PMU Sources for.
               Default: Model of the current CPU
      -OutputDirectory: Folder of output and work files.
               Default: Current Working Directory
      -InputFile: Supplies an optional JSON file to be used.
               Default: Downloaded JSON file
      -Description: Include descriptions of the PMU events in the output.
      -Keep:   Keep the downloaded configuration files. (Works well with: -Verbose)

    .LINK

    https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-pmu-events
    https://devblogs.microsoft.com/performance-diagnostics/recording-hardware-performance-pmu-events-with-complete-examples/
    https://github.com/intel/perfmon/blob/main/README.md
    https://www.intel.com/content/www/us/en/develop/documentation/vtune-cookbook/top/methodologies/top-down-microarchitecture-analysis-method.html
#>

[CmdletBinding()]
Param (
    [Int32]$family = -1,
    [Int32]$model = -1,
    [string]$outputDirectory = $pwd,
    [string]$inputFile = [String]::Empty,
    [switch]$Description,
    [switch]$Keep
)


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
            Write-Verbose "Language Mode: $_"
            break
        }
        { $LangBlock -contains $_ }
        {
            Write-Warning "The current  Language Mode is: $_"
            Write-Warning "The required Language Mode is: $LangAllow"
            [string[]]$Config = $PSSessionConfigurationName -split '/'
            Write-Warning "The current session configuration is: $($Config[-1])"
            Write-Warning "See: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_language_modes"
            Write-Warning ""
            Write-Error "Incompatible Language Mode" # should halt
            exit 1
            break
        }
        default
        {
            Write-Warning "Unrecognized language mode: $_"
            break
        }
    }
}


<#
    Warn if Processor Monitor Counters are not available on this device.
#>
function CheckPMC
{
    $Counters = $Null
    $TempFile = New-TemporaryFile

    try {
        Start-Process -FilePath 'WPR.exe' -ArgumentList '-PMCSources' -NoNewWindow -Wait -RedirectStandardOutput $TempFile -ErrorAction:Stop
        $Counters = Get-Content -LiteralPath $TempFile | Select-String -Pattern '\d' # 0-9
    }
    catch {
        $Counters = ""
    }

    Remove-Item $TempFile

    Write-Verbose "$($Counters.Count) Processor Monitor Counters listed for current configuration."
    if ($Counters.Count -gt 2) { return }

    Write-Output ""
    Write-Warning "This device is not set up for tracing Process Monitor Counters." 
    Write-Warning "It is likely a Virtual Machine or a VM Host."
    Write-Warning "You may need to disable or uninstall HyperV features to access the PMC sources."
    Write-Warning "Expecting multiple counters listed: WPR.exe -PMCSources"
}


function NewConfigObj
{
Param (
    [string]$pmuConfigFilePath,
    [string]$pmuGroupName
)
    return [PSCustomObject]@{ Path = $pmuConfigFilePath; Name = $pmuGroupName }
}


function DownloadConfigFilePath
{
Param (
    [string]$pmuUrlRoot
)

    $webClient = New-Object System.Net.WebClient

    Write-Host "Retrieving Map File..."
    $mapFileUrl = "$pmuUrlRoot/$mapFile"
    $mapFilePath = Join-Path $outputDirectory $mapFile
    Write-Verbose "$mapFileUrl -> $mapFilePath"
    try {
        $webClient.DownloadFile($mapFileUrl, $mapFilePath)
    } catch {
        Write-Warning "Failed to Retrieve file from $mapFileUrl"
        return $null
    }

    #
    # Parse Mapping
    # Note: "hybridcore": at least two different types of CPU core on the same chip
    #

    [PSCustomObject[]]$pmuConfigArray = $Null

    Write-Host "Parsing Map File for Family $family, Model $model..."
    Write-Verbose ("Searching for: FamilyModel = GenuineIntel-{0:X}-{1:X}, EventType = hybrid/core" -f $family,$model)
    $microArchMapping = Import-Csv $mapFilePath
    foreach ($microArchitecture in $microArchMapping) {
        if (($microArchitecture.EventType -eq "core") -or ($microArchitecture.EventType -eq "hybridcore")) {
            $fmComponents = $microArchitecture."Family-model".split("{-}")
            $familyCSV = [convert]::ToInt32($fmComponents[1], 16)
            $modelCSV = [convert]::ToInt32($fmComponents[2], 16)
            if (($family -eq $familyCSV) -and ($model -eq $modelCSV)) {
                $pmuGroupName = $microArchitecture.FileName.split("{/}")[1]
                $microArchUrlPath = $pmuUrlRoot+$microArchitecture.FileName
                $pmuConfigFile = $microArchitecture.FileName.split("{/}")[-1]
                $pmuConfigFilePath = Join-Path $outputDirectory $pmuConfigFile
                $pmuConfigName = $pmuConfigFile -replace '.json'

                Write-Host "Microarchitecture Found: $pmuGroupName/$pmuConfigName"
                Write-Host "Retrieving PMU Source Configs from:"
                Write-Host $microArchUrlPath
                Write-Verbose "$microArchUrlPath -> $pmuConfigFilePath"

                try {
                    $webClient.DownloadFile($microArchUrlPath, $pmuConfigFilePath)
                    $pmuConfigArray += NewConfigObj $pmuConfigFilePath $pmuConfigName
                } catch {
                    Write-Warning "Failed to Retrieve File from:`n$microArchUrlPath"
                }
                if ($microArchitecture.EventType -ne "hybridcore") { break } # hybridcore records come in (adjacent) multiples.
            }
        }
    }

    return $pmuConfigArray
} # DownloadConfigFilePath


# Main

CheckLanguageMode

$intelPmuUrlRoot_GIT = "https://raw.githubusercontent.com/intel/perfmon/main"
$intelPmuUrlRoot_01  = "https://download.01.org/perfmon" # deprecated
$mapFile = "mapfile.csv"
$pmuRegKeyRoot = "HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\WMI\ProfileSource"

#
# If Family or model not specified, use this device's Family/Model
#

if (($family -eq -1) -or ($mode -eq -1)) {
    Write-Output "Getting this CPU's family and model"
    $processorInfo = Get-WmiObject win32_processor
    Write-Output $processorInfo

    if (-not ($processorInfo.Manufacturer -eq "GenuineIntel")) {
        Write-Error "This CPU is not an Intel CPU!"
        return
    }

    $captionComponents = $processorInfo.Caption.split("{ }")
    $family = [convert]::ToInt32($captionComponents[2], 10)
    $model = [convert]::ToInt32($captionComponents[4], 10)
}

if (-not (Test-Path $outputDirectory -ErrorAction:SilentlyContinue)) {
    New-Item $outputDirectory -Type Directory | Out-Null
}

#
# If a custom input file is specified, skip download
#

[PSCustomObject[]]$pmuConfigArray = $null

if (-not $inputFile -eq [String]::Empty) {
    $pmuConfigArray = NewConfigObj $inputFile "Custom"
}
else {
    $pmuConfigArray = DownloadConfigFilePath $intelPmuUrlRoot_GIT

    if (!$pmuConfigArray) {
        $pmuConfigArray = DownloadConfigFilePath $intelPmuUrlRoot_01

        if (!$pmuConfigArray) {
            Write-Warning "Microarchitecture Not Found"
            exit 1
        }

    Write-Output "`nWARNING: Using configuration data from deprecated store: $intelPmuUrlRoot_01`n"
    }
}

[string[]]$regKeyPathArray = $null

foreach ($pmuConfig in $pmuConfigArray) {

    $pmuGroupName = $pmuConfig.Name
    $pmuConfigFilePath = $pmuConfig.Path

    #
    # Create Reg File
    #

    $regKeyPath = Join-Path $outputDirectory "$pmuGroupName.reg"
    $Null = New-Item $regKeyPath -type file -force

    #
    # Construct Reg Key
    #

    Write-Output "Generating $regKeyPath ..."
    "Windows Registry Editor Version 5.00" >> $regKeyPath
    "" >> $regKeyPath
    "[$pmuRegKeyRoot]" >> $regKeyPath
    "" >> $regKeyPath
    "[$pmuRegKeyRoot\$pmuGroupName]" >> $regKeyPath
    "`"Architecture`"=dword:{0:X8}" -f 2 >> $regKeyPath
    "`"Family`"=dword:{0:X8}" -f $family >> $regKeyPath
    "`"Model`"=dword:{0:X8}" -f $model >> $regKeyPath
    "" >> $regKeyPath

    $pmuConfigs = Get-Content $pmuConfigFilePath | ConvertFrom-JSON

    # Format from: https://github.com/intel/perfmon/
    if ($pmuConfigs.Header.Info) {
        Write-Output $pmuConfigs.Header.Info
        Write-Output $pmuConfigs.Header.Copyright
        Write-Output $pmuConfigs.Header.DatePublished
        Write-Output ""
    }
    if ($pmuConfigs.Events.EventCode) {
        $pmuConfigs = $pmuConfigs.Events
    }

    foreach ($pmuConfig in $pmuConfigs) {
        $pmuSourceName = $pmuConfig.EventName
        $msrIndices = $pmuConfig.MSRIndex.split(',')
        $msrIndex = [int]$msrIndices[0]
        $counterMask = [int]$pmuConfig.CounterMask
        $counterMaskInvert = [int]$pmuConfig.Invert
        $anyThread = [int]$pmuConfig.AnyThread
        $edgeDetect = [int]$pmuConfig.EdgeDetect
        $eventCodes = $pmuConfig.EventCode.split(',')

        # Describe the reason to not process this counter, if any.

        $reason = $Null
        if ($pmuConfig.Counter -Match "Fixed") {
            $reason = $pmuConfig.Counter
        } elseif ($msrIndex -ne 0) {
            $reason = "MsrIndex not currently supported" # Multiple values also occur.
            # MSR = Model-Specific Register
            # See MsrIndex here: https://github.com/intel/perfmon/blob/main/README.md
        } elseif ($eventCodes.Count -gt 1) {
            $reason = "Multiple Event Codes not currently supported"
        }

        if ($reason) {

            #
            # Counter either uses fixed counter or needs extra MSRs to configure.
            #
            # Currently all of these fields are not supported.
            #

            $reason = "Skipping $pmuSourceName due to: $reason"

            if ($Description) {
                Write-Output $reason
                if ($pmuConfig.BriefDescription) { Write-Output $pmuConfig.BriefDescription; Write-Output "" }
            } else {
                Write-Verbose $reason
            }

            continue;
        }

    if ($Description) {
            Write-Output "Adding $pmuSourceName"
        } else {
            Write-Verbose "Adding $pmuSourceName"
        }

        $eventCode = [convert]::ToInt32($eventCodes[0], 16)
        $unit = [convert]::ToInt32($pmuConfig.UMask, 16)
        $interval = [convert]::ToInt32($pmuConfig.SampleAfterValue, 16)
        "[$pmuRegKeyRoot\$pmuGroupName\$pmuSourceName]" >> $regKeyPath
        "`"Event`"=dword:{0:X8}" -f $eventCode >> $regKeyPath
        "`"Unit`"=dword:{0:X8}" -f $unit >> $regKeyPath
        "`"Interval`"=dword:{0:X8}" -f $interval >> $regKeyPath
        if (-not ($counterMask -eq 0)) {
            "`"CMask`"=dword:{0:X8}" -f $counterMask >> $regKeyPath
        }
        if (-not ($counterMaskInvert -eq 0)) {
            "`"CMaskInvert`"=dword:{0:X8}" -f $counterMaskInvert >> $regKeyPath
        }
        if (-not ($anyThread -eq 0)) {
            "`"AnyThread`"=dword:{0:X8}" -f $anyThread >> $regKeyPath
        }
        if (-not ($edgeDetect -eq 0)) {
            "`"EdgeDetect`"=dword:{0:X8}" -f $edgeDetect >> $regKeyPath
        }
        if ($Description) {
            if ($pmuConfig.PublicDescription) {
                "`"Description`"=`"$($pmuConfig.PublicDescription)`"" >> $regKeyPath
            }
            if ($pmuConfig.BriefDescription) {
                # (Default) key value
                "@=`"$($pmuConfig.BriefDescription)`"" >> $regKeyPath
                Write-Output $pmuConfig.BriefDescription
            }
            Write-Output ""
        }

        "" >> $regKeyPath

    } # foreach in $pmuConfigs

    $regKeyPathArray += $regKeyPath

    #
    # Cleanup
    #

    if ( !$inputFile -and !$Keep ) {
        Write-Output "Clean Up...`n"

        $mapFilePath = Join-Path $outputDirectory $mapFile

        if (Test-Path $mapFilePath -ErrorAction:SilentlyContinue) {
            Remove-Item $mapFilePath
        }

        if (Test-Path $pmuConfigFilePath -ErrorAction:SilentlyContinue) {
            Remove-Item $pmuConfigFilePath
        }
    }

} # foreach in $pmuConfigArray

Write-Output "Complete!`n"
foreach ($regKeyPath in $regKeyPathArray)
{
    Write-Output "Run: RegEdit `"$regKeyPath`""
}
Write-Output "Then restart the OS."
Write-Output "Finally, list the available counters: wpr -PMCSources"

if ($Description)
{
    Write-Output "`nCounter descriptions can be found in:"
    foreach ($regKeyPath in $regKeyPathArray) {
        Write-Output "`t$regKeyPath"
    }
    Write-Output "or in the registry:"
    foreach ($pmuConfig in $pmuConfigArray) {
        Write-Output "`t$pmuRegKeyRoot\$($pmuConfig.Name)"
    }
}

CheckPMC
