@echo off
rem Entangle - build a single binary (framework-dependent: needs the .NET 10 SDK
rem to build, and the ASP.NET Core 10 runtime on the machine that runs it).
rem
rem   build.cmd             build publish\<rid>\entangle.exe
rem   build.cmd test        run the test suite
rem   build.cmd clean       remove build output
rem   build.cmd win-arm64   build for an explicit runtime identifier
rem
rem The runtime identifier defaults to win-x64 (win-arm64 on ARM64 hosts).

setlocal
set "PROJECT=Entangle.csproj"
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=build"

if /i "%TARGET%"=="test" goto test
if /i "%TARGET%"=="clean" goto clean
if /i "%TARGET%"=="build" goto build
rem anything else is treated as a runtime identifier
goto build

:build
set "RID=%~1"
if /i "%RID%"=="build" set "RID="
if "%RID%"=="" (
    if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" (
        set "RID=win-arm64"
    ) else (
        set "RID=win-x64"
    )
)
set "OUT=publish\%RID%"
dotnet publish "%PROJECT%" -c Release -r "%RID%" --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUT%"
if errorlevel 1 exit /b 1
del /q "%OUT%\*.pdb" "%OUT%\*.staticwebassets.endpoints.json" "%OUT%\appsettings.Development.json" 2>nul
echo.
echo built %OUT%\entangle.exe
exit /b 0

:test
dotnet test Entangle.Tests\Entangle.Tests.csproj
exit /b %errorlevel%

:clean
if exist publish rmdir /s /q publish
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist Entangle.Tests\bin rmdir /s /q Entangle.Tests\bin
if exist Entangle.Tests\obj rmdir /s /q Entangle.Tests\obj
exit /b 0
