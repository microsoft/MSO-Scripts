@echo off

REM Copyright (c) Microsoft Corporation.
REM Licensed under the MIT License.

setlocal

REM This is the kernel event which collects the PMC events.
REM Run: XPerf -providers K
set _COLLECTOR=CSWITCH

REM Run: XPerf -help stackwalk
set _CSTACK=CSWITCH

set _ETL_PATH="%LOCALAPPDATA%\MSO-Scripts\PMC_XPerf.etl"

call :ParsePMC TotalCycles InstructionRetired

if [%1]==[-?] goto :Usage
if [%1]==[/?] goto :Usage

if not [%1]==[]	call :ParsePMC %*

set _TEMP_PATH="%TEMP%\PMC_xperfT.etl"
set _XPERF_PARAMS=-on PROC_THREAD+LOADER+%_COLLECTOR% -stackwalk %_CSTACK% -Pmc %_PROFILE% %_COLLECTOR% strict -f %_TEMP_PATH%

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

echo Exercise the code. Tracing has begun: %_PROFILE% on %_COLLECTOR%
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
:PMCLoop
shift
if [%1]==[] exit /b
set _PROFILE=%_PROFILE%,%1
goto :PMCLoop

:TestAdmin
net session >nul 2>&1
exit /b errorlevel

:Usage
echo Usage:
echo 	%~nx0
echo 	Tracing: %_PROFILE%
echo 	Collected at each %_COLLECTOR%
echo 	This is the Cycles per Instruction (CPI) configuration understood by WPA.
echo:
echo 	%~nx0 [PMC1 [PMC2 ...]]
echo 	Tracing: PMC1,PMC2,...
echo:
echo 	PMC = Processor/Performance Monitor Counter
echo 	For a list of available PMCs, run: XPerf -PMCSources
echo 	To expand the list, run: Generate-PmuRegFile
if not defined OVerbose echo: & echo 	Verbose output: set OVerbose=1
echo:
echo 	https://github.com/microsoft/MSO-Scripts/wiki/Customize-Tracing#pmu
echo 	https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-pmu-events
exit /b 1
