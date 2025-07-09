Copyright (c) Microsoft Corporation. Licensed under the MIT License.

GOALS

- It's easy to capture basic ETW traces of Office and other apps: CPU, Wait Analysis, Memory, Handles, File & Disk I/O, etc.
- It's easy to analyze and understand the results.
- It's easy to modify and to configure many combinations of data providers.
- It works on Windows 10/11+, and as far back as Windows 7 / Server 2008-R2.
- It requires no extra executables to be installed for trace collection (if Windows 10+).
- It works with PowerShell (v2+), WPR (Windows Performance Recorder), and WPA (Windows Performance Analyzer).

CUSTOMIZING RECORDING PROFILES

MSO-Scripts includes a set of optimized recording profiles that will work well for many situations.
They can also be customized.

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

OPTIONAL ENVIRONMENT VARIABLES

	TRACE_PATH
	Path which receives generated traces and intermediate files. (Default = %LocalAppData%\MSO-Scripts)

	WPT_PATH
	Find wpr.exe and/or wpa.exe here from the Windows Performance Toolkit.

	WPT_WPRP
	Semi-colon separated list of additional WPR Recording Profiles (in any of the three formats listed above).

	WPT_XPERF
	Plus-sign separated list of additional ETW Providers (in XPerf -ON format): GUID|Name:KeywordFlags:Level:Stack

	WPT_MODE
	Special tracing mode - Shutdown

	_NT_SYMBOL_PATH
	Defines where to find and store symbol files (*.PDB). *** These files can be HUGE! ***

	_NT_SYMCACHE_PATH
	Defines where to find and store optimized symbol files (*.symcache).

SYMBOL RESOLUTION

	https://github.com/microsoft/MSO-Scripts/wiki/Symbol-Resolution#native

	https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#optimize

	If the _NT_SYM*_PATH environment variables are not set, the script for launching WPA will set default values.
		_NT_SYMBOL_PATH=cache*C:\Symbols;srv*https://msdl.microsoft.com/download/symbols
		_NT_SYMCACHE_PATH=C:\SymCache

OLDER VERSIONS of the Windows Performance Toolkit WPT / WPA / WPR

	Scripts under the PreWin10 subfolder are for earlier versions of the Windows Performance Toolkit (WPT).
	They should run automatically, or they can be run directly from the PreWin10 subfolder.
