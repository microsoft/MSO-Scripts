<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	SymbolScan [-Allow | -Prevent] [-Verbose]
	  -Allow: Let Windows Defender scan symbol and trace files (default).
	  -Prevent: Windows Defender should not scan symbol and trace files (for speed).

	.DESCRIPTION

	Virus scanners may slow down symbol resolution by scanning the potentially large symbol files downloaded/created by WPA: *.pdb, *.symcache
	(Windows Performance Analyzer [WPA] downloads *.pdb files as *.error, and *.symcache files as *.pending, then renames them once completed.)

	To increase performance, add the symbol file extensions to the Windows Defender Exclusion List: .pdb, .symcache, .pending, .error
	Also add the ETW log extensions: .etl, .wpapk

	.LINK

	https://learn.microsoft.com/en-us/powershell/module/defender/add-mppreference
#>

Param (
	# Windows Defender should scan trace/symbol files.
	#[Parameter(ParameterSetName="Scan")]
	[switch]$Allow,

	# Windows Defender should not scan trace/symbol files.
	[Parameter(ParameterSetName="NoScan")]
	[switch]$Prevent
)


function GetArgs { return $Args }


[array]$SymbolFileExtensions = GetArgs .etl .wpapk .pdb .symcache .pending .error


<#
	Determine whether this script has Administrator privileges.
#>
function CheckAdminPrivilege
{
	$Principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
	$Administrator = [Security.Principal.WindowsBuiltInRole]::Administrator
	return $Principal.IsInRole($Administrator)
}


<#
	Add the file extensions to the Windows Defender Exclusion List: .etl, .wpapk, .pdb, .symcache, .pending, .error
	The -Undo switch removes these file extensions from the Wndows Defender Exclusion List.
#>
function ExcludeSymbolExtensions
{
Param(
	[switch]$Undo
)
	Write-Verbose "(Get-MpPreference).ExclusionExtension"
	[array]$ExclusionExtensions = (Get-MPPreference -ErrorAction:SilentlyContinue).ExclusionExtension

	# Get the intersection of the Symbol File Extensions with the current list of file extension exclusions.

	[array]$CommonExtensions = @()
	if ($ExclusionExtensions.length -ne 0)
	{
		Write-Verbose "$ExclusionExtensions"

		$CommonExtensions = Compare-Object -IncludeEqual -ExcludeDifferent -PassThru $SymbolFileExtensions $ExclusionExtensions
	}

	if ($Undo)
	{
		if ($CommonExtensions.length -eq 0) { return } # nothing to remove

		# Remove Symbol File Exclusions

		Write-Output "Allowing Windows Defender to scan the Symbol/Trace File Extensions:"
		Write-Output $CommonExtensions
		Write-Verbose "Remove-MpPreference -ExclusionExtension $CommonExtensions"
		Remove-MPPreference -ErrorAction:SilentlyContinue -ExclusionExtension $CommonExtensions >$Null
	}
	else
	{
		if ($CommonExtensions.length -eq $SymbolFileExtensions.length) { return } # nothing to add

		# Add Symbol File Exclusions

		Write-Verbose "Add-MpPreference -ExclusionExtension $SymbolFileExtensions"
		Add-MpPreference -ErrorAction:SilentlyContinue -ExclusionExtension $SymbolFileExtensions >$Null
	}

	# Double-check the result.

	Write-Verbose "(Get-MpPreference).ExclusionExtension"
	[array]$ExclusionExtensions = (Get-MPPreference -ErrorAction:SilentlyContinue).ExclusionExtension

	if ($ExclusionExtensions.length -eq 0) { return }

	Write-Verbose "$ExclusionExtensions"

	[array]$CommonExtensions = Compare-Object -IncludeEqual -ExcludeDifferent -PassThru $SymbolFileExtensions $ExclusionExtensions

	if ($CommonExtensions.length -eq 0) { return }

	if ($Undo)
	{
		Write-Output "Warning: These Symbol/Trace File Extensions remain on the Windows Defender Exclusion List:" $CommonExtensions
	}
	else
	{
		Write-Output "For speed, these Symbol/Trace File Extensions will not be scanned by Windows Defender:"
		Write-Output $CommonExtensions
		Write-Output $Null
		Write-Output "To undo this, run: $($script:MyInvocation.InvocationName -Replace ".ps1$") -Allow"
	}
} # ExcludeSymbolExtensions


function ListSymbolExtensions
{
	Write-Verbose "(Get-MpPreference).ExclusionExtension"
	[array]$ExclusionExtensions = (Get-MPPreference -ErrorAction:SilentlyContinue).ExclusionExtension

	if ($ExclusionExtensions.length -ne 0)
	{
		Write-Verbose "$ExclusionExtensions"

		[array]$CommonExtensions = Compare-Object -IncludeEqual -ExcludeDifferent -PassThru $SymbolFileExtensions $ExclusionExtensions

		if ($CommonExtensions.length -ne 0)
		{
			Write-Output "These Symbol/Trace File Extensions will not be scanned by Windows Defender:"
			Write-Output $CommonExtensions
			return
		}
	}

	Write-Output "There are no Symbol/Trace File Extensions on the Windows Defender Exclusion List."
}


# Main

if ([Environment]::OSVersion.Version.Major -lt 10)
{
	Write-Output "Windows 10+ is required."
	return
}

if (!(CheckAdminPrivilege))
{
	Write-Output "Administrator Privilege is required."
	return
}

if ($Prevent) { ExcludeSymbolExtensions }
elseif ($Allow) { ExcludeSymbolExtensions -Undo }
else { ListSymbolExtensions }
Write-Output $Null
