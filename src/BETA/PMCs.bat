@echo off

REM Copyright (c) Microsoft Corporation.
REM Licensed under the MIT License.

setlocal

set _ETL_PATH="%LOCALAPPDATA%\MSO-Scripts\PMCs_XPerf.etl"

REM Min Interval is usually 4096. Run: XPerf -PMCSources
set _INTERVAL_=4096

call :ParsePMC CacheMisses BranchMispredictions

if [%1]==[-?] goto :Usage
if [%1]==[/?] goto :Usage

if not [%1]==[] call :ParsePMC %*

set _TEMP_PATH="%TEMP%\PMCs_xperfT.etl"
set _XPERF_PARAMS=-on PROC_THREAD+LOADER+PMC_PROFILE -StackWalk PmcInterrupt -PmcProfile %_PROFILE% %_PROFINT% -f %_TEMP_PATH%

if defined OVerbose echo xperf %_XPERF_PARAMS%
call xperf %_XPERF_PARAMS% || (
  if not defined OVerbose echo xperf %_XPERF_PARAMS%
  echo:
  call :TestAdmin
  if errorlevel 1 echo Be sure to run with Administrator permissions.& exit /b 2
  echo Run: XPerf -PMCSources
  echo Check that the counter is listed: %_PROFILE%
  echo If there is only one entry listed then this may be a HyperV server.
  exit /b 3
)

echo Tracing has begun. Exercise the code.
@pause
echo Processing...

set _XPERF_PARAMS=-stop -d %_ETL_PATH%
if defined OVerbose echo xperf %_XPERF_PARAMS%
call xperf %_XPERF_PARAMS% || (
  if not defined OVerbose echo xperf %_XPERF_PARAMS%
  exit /b errorlevel
)

set _WPA_PARAMS=%_ETL_PATH% -symbols
if not defined _NT_SYMBOL_PATH if defined _NT_SYMCACHE_PATH (
	set _WPA_PARAMS=%_WPA_PARAMS% -symcacheonly
	if defined OVerbose echo: & echo Enabling only SymCache: %_NT_SYMCACHE_PATH%
	if defined OVerbose echo https://learn.microsoft.com/en-us/windows-hardware/test/wpt/loading-symbols#symcache-path
)
set _WPA_PARAMS=%_WPA_PARAMS% -profile "%~dp0WPAP\CPUCounters.wpaProfile"
echo:
echo Launching WPA
if defined OVerbose echo wpa %_WPA_PARAMS%
start "WPA" /MAX /ABOVENORMAL wpa %_WPA_PARAMS%

exit /b errorlevel

:ParsePMC
REM Expect names of Processor Monitor Counters. Run: WPR -PMCSources
set _PROFILE=%1
set _PROFINT=-SetProfInt %1 4096
:PMCLoop
shift
if [%1]==[] exit /b
set _PROFILE=%_PROFILE%,%1
set _PROFINT=%_PROFINT% -SetProfInt %1 %_INTERVAL_%
goto :PMCLoop

:TestAdmin
net session >nul 2>&1
exit /b errorlevel

:Usage
echo Usage:
echo 	%~nx0
echo 	Sampled profiling: %_PROFILE%
echo:
echo 	%~nx0 [PMC1 [PMC2 ...]]
echo 	Sampled profiling: PMC1,PMC2,...
echo:
echo 	PMC = Processor/Performance Monitor Counter
echo 	For a list of available PMCs, run: XPerf -PMCSources
echo 	To expand the list, run: Generate-PmuRegFile
if not defined OVerbose echo: & echo 	Verbose output: set OVerbose=1
echo:
echo 	https://github.com/microsoft/MSO-Scripts/wiki/Customize-Tracing#pmu
echo 	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-pmu-events
exit /b 1
