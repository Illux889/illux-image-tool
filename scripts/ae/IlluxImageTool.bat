@echo off
rem Illux Image Tool - Esko Automation Engine wrapper (Batch File script).
rem
rem AE gives this script:  %1 = input files (joined by ";"),  %2 = output folder,  %3... = Script Parameters.
rem It waits while an update is being installed, runs IlluxImageTool.exe and hands its exit code
rem back to AE (0 = OK, 1 = Warning, 2 = Error).
rem
rem Test-only settings (never needed in production):
rem   ILLUX_IMAGETOOL_EXE                 path of the exe to run instead of the installed one
rem   ILLUX_IMAGETOOL_ROOT                folder that holds updating.flag instead of C:\ProgramData\Illux Image Tool
rem   ILLUX_IMAGETOOL_WAIT_SECONDS        seconds between two checks of updating.flag (default 5)
rem   ILLUX_IMAGETOOL_WAIT_MAX_SECONDS    seconds to wait for an update at most (default 600 = 10 minutes)
setlocal EnableExtensions DisableDelayedExpansion

set "EXE=%ILLUX_IMAGETOOL_EXE%"
if defined EXE goto exe_known
set "PF=%ProgramW6432%"
if not defined PF set "PF=%ProgramFiles%"
set "EXE=%PF%\Illux Image Tool\IlluxImageTool.exe"
:exe_known

set "FLAG=%ILLUX_IMAGETOOL_ROOT%"
if not defined FLAG set "FLAG=%ProgramData%\Illux Image Tool"
set "FLAG=%FLAG%\updating.flag"

set "WAIT=%ILLUX_IMAGETOOL_WAIT_SECONDS%"
if not defined WAIT set "WAIT=5"
set "WAITMAX=%ILLUX_IMAGETOOL_WAIT_MAX_SECONDS%"
if not defined WAITMAX set "WAITMAX=600"
set /a PINGS=WAIT+1
set /a WAITED=0

rem --- wait while an update is being installed ---
if not exist "%FLAG%" goto wait_done
echo Waiting for the update of Illux Image Tool to finish...
:wait_loop
if not exist "%FLAG%" goto wait_done
if %WAITED% GEQ %WAITMAX% goto wait_timeout
rem "ping -n N" waits about N-1 seconds; unlike "timeout" it also works without a console.
ping -n %PINGS% 127.0.0.1 >nul
set /a WAITED+=WAIT
goto wait_loop
:wait_timeout
echo ERROR: update did not finish
exit /b 2
:wait_done

if exist "%EXE%" goto exe_found
echo ERROR: IlluxImageTool.exe not found at "%EXE%"
exit /b 2
:exe_found

rem --- collect %1 and %2 ---
set "INPUTS=%~1"
set "OUTDIR=%~2"
rem A trailing backslash would escape the closing quote of the argument; "\." names the same folder.
if defined OUTDIR if "%OUTDIR:~-1%"=="\" set "OUTDIR=%OUTDIR%."
shift
shift

rem --- collect the Script Parameters (%3, %4, ... ) one by one; a batch file can only name %1-%9 directly ---
rem cmd splits an unquoted name=value at the "=" (output=pdf arrives as "output" and "pdf"), so these are joined again:
rem output, quality, filter and missingppi take the next token; upsample only when the next token is all digits.
set "REST="
:collect
if [%1]==[] goto run
if /i "%~1"=="output" goto join_value
if /i "%~1"=="quality" goto join_value
if /i "%~1"=="filter" goto join_value
if /i "%~1"=="missingppi" goto join_value
if /i "%~1"=="upsample" goto join_number
:plain
set REST=%REST% %1
shift
goto collect

:join_value
if [%2]==[] goto plain
set REST=%REST% %1=%2
shift
shift
goto collect

:join_number
if [%2]==[] goto plain
if "%~2"=="" goto plain
for /f "delims=0123456789" %%a in ("%~2") do goto plain
set REST=%REST% %1=%2
shift
shift
goto collect

:run
"%EXE%" ae "%INPUTS%" "%OUTDIR%"%REST%
set "RC=%errorlevel%"
rem AE knows 0 = OK, 1 = Warning, 2 = Error; anything else (a crash, a missing DLL) is an Error.
if "%RC%"=="0" exit /b 0
if "%RC%"=="1" exit /b 1
exit /b 2
