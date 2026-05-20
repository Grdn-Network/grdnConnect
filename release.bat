@echo off
setlocal enabledelayedexpansion

echo ===================================
echo   GRDN Connect Release Tool
echo ===================================
echo.

:: Make sure we're on the right branch
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "BRANCH=%%b"
if /i not "%BRANCH%"=="main" (
    echo WARNING: You are on branch "%BRANCH%", not main.
    set /p CONT="Continue anyway? (y/n): "
    if /i not "!CONT!"=="y" exit /b 1
    echo.
)

:: Abort if there are uncommitted changes
git diff --quiet --exit-code 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: You have unstaged changes. Commit or stash them first.
    pause & exit /b 1
)
git diff --cached --quiet --exit-code 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: You have staged but uncommitted changes. Commit them first.
    pause & exit /b 1
)

:: Show the last few tags so the user knows where they left off
echo Last releases:
git tag --sort=-version:refname | head -5 2>nul || git tag --sort=-version:refname
echo.

:: Prompt for version
set /p VERSION="Enter version tag (e.g. v1.2.0): "
if "%VERSION%"=="" (
    echo No version entered. Aborting.
    pause & exit /b 1
)

:: Validate format starts with v
echo %VERSION% | findstr /r "^v[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Version must be in the format v1.2.3
    pause & exit /b 1
)

:: Confirm before doing anything
echo.
echo  Tag   : %VERSION%
echo  Branch: %BRANCH%
echo  Remote: origin
echo.
set /p CONFIRM="Push this release? (y/n): "
if /i not "%CONFIRM%"=="y" (
    echo Aborted.
    pause & exit /b 0
)

:: Create and push the tag
echo.
echo Creating tag %VERSION%...
git tag %VERSION%
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to create tag. Does %VERSION% already exist?
    pause & exit /b 1
)

echo Pushing tag to origin...
git push origin %VERSION%
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to push tag. Check your remote connection.
    git tag -d %VERSION%
    pause & exit /b 1
)

echo.
echo ===================================
echo  Release %VERSION% is on its way!
echo  GitHub Actions is now building and
echo  publishing the release automatically.
echo ===================================
echo.
pause
