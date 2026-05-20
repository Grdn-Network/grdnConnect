@echo off
setlocal enabledelayedexpansion

:: Find latest dotnet SDK version
set "DOTNET_ROOT=C:\Program Files\dotnet\sdk"
set "LATEST="
for /d %%v in ("%DOTNET_ROOT%\*") do set "LATEST=%%v"

if not defined LATEST (
    echo ERROR: dotnet SDK not found at %DOTNET_ROOT%
    exit /b 1
)

set "CSC=!LATEST!\Roslyn\bincore\csc.dll"
echo Using Roslyn from: !CSC!

:: Ensure output dir exists
if not exist "bin\Release" mkdir "bin\Release"

:: Compile directly with Roslyn (C# 9, full control over references)
dotnet "!CSC!" ^
    /target:library ^
    /out:"bin\Release\GRDNConnect.dll" ^
    /langversion:9 ^
    /optimize+ ^
    /unsafe+ ^
    /nostdlib- ^
    /reference:lib\Assembly-CSharp.dll ^
    /reference:lib\DV.ThingTypes.dll ^
    /reference:lib\DV.Inventory.dll ^
    /reference:lib\UnityModManager.dll ^
    /reference:lib\UnityEngine.dll ^
    /reference:lib\UnityEngine.CoreModule.dll ^
    /reference:lib\UnityEngine.IMGUIModule.dll ^
    /reference:lib\DV.Utils.dll ^
    Main.cs Settings.cs GRDNConnectBehaviour.cs JobCompletionHelper.cs

if %ERRORLEVEL% EQU 0 (
    echo.
    echo Build succeeded: bin\Release\GRDNConnect.dll
) else (
    echo.
    echo Build FAILED with error %ERRORLEVEL%
)
