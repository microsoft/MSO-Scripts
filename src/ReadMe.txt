Copyright (c) Microsoft Corporation. Licensed under the MIT License.

GOALS

- It's easy to capture basic ETW traces of Office and other apps: CPU, Wait Analysis, Memory, Handles, File & Disk I/O, etc.
- It's easy to analyze and understand the results.
- It's easy to modify and to configure many combinations of data providers.
- It works on Windows 10+, and as far back as Windows 7 / Server 2008-R2.
- It requires no extra executables to be installed for trace collection (if Windows 10+).
- It works with PowerShell (v2+), WPR (Windows Performance Recorder), and WPA (Windows Performance Analyzer).

CUSTOMIZING RECORDING PROFILES

In Trace*.ps1, customize the recording profiles here:

	$TraceParams =
	@{
		RecordingProfiles =
		@(
			# This is the base recording profile:
			".\WPRP\<RecordingProfile>.wprp!<ProfileName>"

			# *** Additional recording profile strings go here. ***
		)

		...
	}

Note that no .wprp file can be used more than once in a profiling session.

Recording Profiles (Trace Collection Configuration Files) can take several forms:

1) Local profiles: ".\WPRP\SomeProfile.wprp!ProfileName"
   To list available profile names, run: WPR -profiles .\WPRP\<SomeProfile>.wprp

	".\WPRP\FileDiskIO.wprp!FileIO"
	".\WPRP\Handles.wprp!KernelHandles"

   Certain .wprp files have a version number in their name, which corresponds to the minimum version of WPR.
   For example, if the WPR version is 10.0.15002 or greater, then the newer version of Network.wprp will be used:

	".\WPRP\Network.wprp!NetworkFull" becomes: ".\WPRP\Network.15002.wprp!NetworkFull"

2) OR built-in profiles
   To see a complete list, run: WPR -profiles

	"Registry"
	"DotNet"

3) OR other WPRP Recording Profiles: "<full_path>\MyRecordingProfile.wprp!ProfileName"
   To see a list of available profiles, run: WPR -profiles <full_path>\MyRecordingProfile.wprp
   https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-profile-xml-reference

	"c:\MyProfiles\MyRecordingProfile.wprp!ProfileName"

ENVIRONMENT VARIABLES

	TEMP
	Intermediate and final trace files go here.

	WPT_PATH
	Optional: Find wpr.exe and/or wpa.exe here from the Windows Performance Toolkit.

	WPT_WPRP
	Optional: Semi-colon separated list of additional WPR Recording Profiles (in any of the three formats listed above).

	WPT_XPERF
	Optional: Plus-sign separated list of additional ETW Providers (in XPerf -ON format): GUID|Name:KeywordFlags:Level:Stack

	_NT_SYMBOL_PATH
	Defines where to find and store symbol files (*.PDB). *** These files can be HUGE! ***
	If this is not defined, a default symbol path will be provided.

	_NT_SYMCACHE_PATH
	Defines where to find and store optimized symbol files (*.symcache).
	If this is not defined, a default symcache path will be provided.

SYMBOL RESOLUTION

	If the _NT_SYM*_PATH environment variables are not set, the script for launching WPA will set default values.
	The default storage drive is C:.  If it has limited capacity, set the environment variables manually.
	To choose the cache disk, but otherwise accept the default symbol path:
		set _NT_SYMBOL_PATH=cache*D:\symbols

	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/loading-symbols
	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/load-symbols-or-configure-symbol-paths

	To reset the symbol paths (using a symbol cache folder other than c:\symbols), these CMD-prompt commands will do it:
		set _NT_SYMBOL_PATH=cache*D:\symbols
		set _NT_SYMCACHE_PATH=
		ResetWPA -ALL
	Then use a "Trace* View" command to relaunch WPA with default symbol paths:
		TraceCPU View

OPTIMIZE SYMBOL LOADING

	Virus scanners might slow down symbol resolution by scanning the HUGE symbol files downloaded/created by WPA: *.pdb, *.symcache
	WPA downloads *.pdb files as *.error, then renames them once completed.  It may also download *.symcache files as *.pending, then rename them once completed.

	For performance (Windows 10+ only), you can add the symbol file extensions to the Windows Defender Exclusion List:
		SymbolScan -prevent
	This prevents Windows Defender from scanning files with these extensions:
		*.symcache, *.pdb, *.error, *.pending
		Also the trace file extension: *.etl

	Return Windows Defender to normal scanning behavior for those extensions:
		SymbolScan -allow

	Show Windows Defender's current status regarding those extensions:
		SymbolScan

OLDER VERSIONS of the Windows Performance Toolkit WPT / WPA / WPR

	Scripts under the PreWin10 subfolder are for earlier versions of the Windows Performance Toolkit (WPT).
	They should run automatically, or they can be run directly from the PreWin10 subfolder.
