<#
	.NOTES

	Copyright (c) Microsoft Corporation.
	Licensed under the MIT License.

	.SYNOPSIS

	Reset the Windows Performance Analyzer (WPA)
	by removing data and configuration files.

	Preserves Window Preferences (including the Symbol Paths) if not -All.
	Saves a copy of WindowPreferences.xml to %TEMP%.
#>
[CmdletBinding()]
Param (
	# Remove All: Do not preserve Window Preferences or Symbol Settings.
	[switch]$All = $false
)

$WPA_1A = "$env:TEMP\WpaPackage"
$WPA_1B = "$env:TEMP\WPA"
$WPA_1C = "$env:TEMP\WPA Recovered Profile"
$WPA_1D = "$env:TEMP\Windows Performance Analyzer"
$WPA_2  = "$env:USERPROFILE\Documents\WPA Files"
$WPA_3  = "$env:LOCALAPPDATA\Windows Performance Analyzer"
$WPA_4  = "$env:LOCALAPPDATA\wpa"
$WPA_PREF = "WindowPreferences.xml"


function DeleteFolder
{
Param (
	[string]$Path
)
	Write-Verbose "Testing: $Path"

	if (Test-Path $Path -ErrorAction:SilentlyContinue -ErrorVariable Err)
	{
		Write-Output "Removing: $Path"

		# Avoid overlong path issues by using CMD's rmdir:
		Invoke-Expression "cmd.exe /c rmdir /s /q `"$Path`" 2>&1" -ErrorVariable Err >$Null
	#	rmdir -Recurse "$Path" -ErrorAction:SilentlyContinue -ErrorVariable Err
	}

	if ($Err)
	{
		$Err = $($Err[0] -split "`n") # convert to one line
		Write-Output "$Err : $Path"
	}
}

# Main

Write-Output "Resetting WPA settings and storage."

DeleteFolder $WPA_1A

DeleteFolder $WPA_1B

DeleteFolder $WPA_1C

DeleteFolder $WPA_1D

DeleteFolder $WPA_2

if ($All)
{
	DeleteFolder $WPA_4
}

# Delete the "Windows Performance Analyzer" folder, but optionally save/restore WPA Window Settings: WindowsPreferences.xml

$WPA_PREF_Src = "$WPA_3\$WPA_PREF"
$WPA_PREF_Dst = "$env:TEMP\$WPA_PREF"

$Err = $True
if (Test-Path -PathType Leaf $WPA_PREF_Src -ErrorAction:SilentlyContinue)
{
	# Save a copy of WindowPreferences.xml to %TEMP%.
	# Possibly restore it.

	Copy-Item $WPA_PREF_Src -Destination $WPA_PREF_Dst -Force -ErrorAction:SilentlyContinue -ErrorVariable Err
}

DeleteFolder $WPA_3

if (!$Err)
{
	# A copy of the WPA Windows Settings file was successfully saved.
	# Either restore it or (verbose) note that it was saved.

	$dir = mkdir $WPA_3 -ErrorAction:SilentlyContinue

	if (!$All)
	{
		Copy-Item $WPA_PREF_Dst $WPA_PREF_Src -ErrorAction:SilentlyContinue -ErrorVariable Err2

		if (!$Err2)
		{
			Write-Output "Restoring WPA Window Settings"
		}
		else
		{
			Write-Output "WPA Window Settings could not be restored to: `"$WPA_PREF_Src`""
			Write-Output "A copy was saved to: `"$WPA_PREF_Dst`""
		}
	}
	else
	{
		Write-Verbose "WPA Windows Settings were saved to: `"$WPA_PREF_Dst`""
		Write-Verbose "From: `"$WPA_PREF_Src`""
	}
}
