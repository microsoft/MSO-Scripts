@echo off

REM Copyright (c) Microsoft Corporation.
REM Licensed under the MIT License.

REM Download symbols based on those referenced in the given ETW log file (.etl).
REM Optionally limit downloads to the given modules (faster).
REM EnvVar OVerbose=1 : report commands executed

setlocal

set _this=%~nx0

if [%1]==[] goto :Usage
if [%1]==[-?] goto :Usage
if [%1]==[/?] goto :Usage
if /i not [%~x1]==[.etl] goto :Usage

set _ETL="%~dpnx1"
if not exist %_ETL% echo Does not exist: %_ETL% & goto :Usage

rem Default symbol resolution paths.
rem See: https://github.com/microsoft/MSO-Scripts/wiki/Symbol-Resolution#native

set SYMSVR_DFT=msdl.microsoft.com
set SYMLINK_DFT=https://msdl.microsoft.com/download/symbols

ping -n 1 %SYMSVR_DFT% >nul
if errorlevel 1 echo WARNING: Not able to contact %SYMSVR_DFT% & echo:

set SYMDIR_DFT=%SystemDrive%\Symbols
set SYMPATH_DFT=cache*%SYMDIR_DFT%;srv*%SYMLINK_DFT%
set SYMCACHE_DFT=%SystemDrive%\symcache

call :SetSymDir

if not defined _NT_SYMBOL_PATH set _NT_SYMBOL_PATH=%SYMPATH_DFT%
if not defined _NT_SYMCACHE_PATH set _NT_SYMCACHE_PATH=%SYMCACHE_DFT%

REM Older versions of XPerf.exe copy PDB files to the current directory, which will be _SYMDIR.

pushd "%_SYMDIR%"
if defined OVerbose echo Current Working Directory: %CD%

echo Downloading and transcoding symbols referenced in: %_ETL%
echo Using: _NT_SYMBOL_PATH=%_NT_SYMBOL_PATH%
echo Using: _NT_SYMCACHE_PATH=%_NT_SYMCACHE_PATH%
echo This could take a while!
echo:

rem XPerf.exe is often adjacent to WPA.exe, or available in the ADK / Windows Performance Toolkit: https://aka.ms/adk

set _filter=findstr /r "bytes.*SYMSRV.*RESULT..0x00000000"
set _filter1=2^>^&1 ^| %_filter%
set _filter2=2^^^>^^^&1 ^^^| %_filter%

if not [%2]==[] if /i not [%2]==[-v] goto :Specific

REM Output:
REM -v : filtered verbose xperf output, else none

set _verbose=verbose
if /i not [%2]==[-v] set _filter1=2^>nul& set _filter2=2^^^>nul& set _verbose=

REM OVerbose and -v removes ALL filters?
REM if defined OVerbose if defined _verbose set _filter1=& set _filter2=

set _cmd=xperf -tle -tti -i %_ETL% -symbols %_verbose% -a symcache -build

if defined OVerbose echo Running: %_cmd% %_filter2%

%_cmd% %_filter1%

REM If _verbose then the errorlevel is from findsym, else it is from xperf.
if not defined _verbose goto :Finished

popd
echo Finished
exit /b 0


:Specific

REM Create a list of available modules within the trace.

shift

set _tempfile="%temp%\_imageid.txt"
set _cmd=xperf -tle -tti -i %_ETL% -a symcache -imageid

if defined OVerbose echo Running: %_cmd% 1^>%_tempfile% 2^>nul

%_cmd% 1>%_tempfile% 2>nul

if not %ERRORLEVEL%==0 goto :Finished

set _Verbose=
set _Modules=

:Loop

if /i [%1]==[-v] set _Verbose=verbose& goto :EndLoop

set _cmd=findstr /i /c:"\"%~1\"" %_tempfile%

if defined OVerbose ( %_cmd% ) else ( %_cmd% 1>nul 2>nul)

if errorlevel 1 (
	echo:
	echo Not found in the trace file: "%~1"
	echo See the list: %_tempfile%
	popd
	exit /b 1
)

set _Modules=%_Modules% "%~1"

:EndLoop
shift
if not [%1]==[] goto :Loop

echo:

if not defined _Modules (
	if defined _Verbose echo List of available modules: %_tempfile% & echo:
	popd
	goto :Usage
)

del %_tempfile% 1>nul 2>nul

rem XPerf.exe is often adjacent to WPA.exe, or available in the ADK / Windows Performance Toolkit: https://aka.ms/adk

REM Output:
REM -v : verbose xperf output, else filtered

if defined _Verbose set _filter1=& set _filter2=

set _cmd=xperf -tle -tti -i %_ETL% -symbols verbose -a symcache -build -image %_Modules%

if defined OVerbose echo Running: %_cmd% %_filter2%

%_cmd% %_filter1%

REM If not defined _Verbose then the errorlevel is from findsym, else it is from xperf.
if defined _Verbose goto :Finished

popd
echo Finished
exit /b 0


:Finished

if not %ERRORLEVEL%==0 (
	echo Failed with %ERRORLEVEL%:
	echo %_cmd%

	REM 0xC0000409 ... suspicious, but intermittent.
	if %ERRORLEVEL%==-1073740791 echo Please try again.

	REM 0xC0000005 ... intermittent access violation.
	if %ERRORLEVEL%==-1073741819 echo Please try again.

	REM 9009 ... MSG_DIR_BAD_COMMAND_OR_FILE
	if %ERRORLEVEL%==9009 (
		echo XPerf was not found. It is often adjacent to WPA.exe, or available in the ADK ^> Windows Performance Toolkit.
		echo WPA: https://apps.microsoft.com/detail/9n0w1b2bxgnz
		echo ADK: https://aka.ms/adk
	)
) else (
	echo Completed without error.
)

popd

exit /b errorlevel


:SetSymDir
REM Older versions of XPerf copy PDBs to the current directory.
REM Set _SYMDIR = the cache folder using _NT_SYMBOL_PATH, which is:
REM   CASE 1: cache*<cachefolder>;sym*http://downloads;...
REM   CASE 2: srv*<cachefolder>*http://downloads;...
REM   CASE 3: <symfolder>;...

if not defined _NT_SYMBOL_PATH goto :FallBack

set _SYMDIR=

REM CASE 1
if /i [%_NT_SYMBOL_PATH:~0,6%]==[cache*] goto :Extract2

REM CASE 2
if /i [%_NT_SYMBOL_PATH:~0,4%]==[srv*] goto :Extract2

REM CASE 3
for /f "tokens=1 delims=;" %%c in ("%_NT_SYMBOL_PATH%") do (
	set _SYMDIR=%%c
)
if defined _SYMDIR goto :PathCheck
goto :FallBack

:Extract2

for /f "tokens=2 delims=*;" %%c in ("%_NT_SYMBOL_PATH%") do (
	set _SYMDIR=%%c
)
if not defined _SYMDIR goto :FallBack

:PathCheck

REM Check for existing "c:\folder"
if [%_SYMDIR:~1,2%]==[:\] if exist "%_SYMDIR%" exit /b 0

:FallBack

set _SYMDIR=%SYMDIR_DFT%
mkdir "%_SYMDIR%" 1>nul 2>nul

exit /b 1


:Usage
echo Usage:   %_this% ^<path^>\^<name^>.etl [Module1 [Module2] ... ] [-v]
echo Example: %_this% c:\mypath\mytrace.etl Excel.exe MSO.dll
echo Example: %_this% c:\mypath\mytrace.etl -v
echo Download symbols for modules referenced by the given ETW trace (.etl), and transcode to SymCache format for WPA.
echo Download all referenced modules, or (faster) the modules listed and dependents: Module1 Module2 ... (-v = verbose)
echo See: https://github.com/microsoft/MSO-Scripts/wiki/Advanced-Symbols#deeper
echo See: https://github.com/microsoft/MSO-Scripts/wiki/Symbol-Resolution#native
echo See: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/xperf/symbols
exit /b 1
