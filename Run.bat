@echo off
setlocal
cd /d "%~dp0"

if /I "%~1"=="--skip-git-update" goto after_git_update

where git >nul 2>nul
if errorlevel 1 (
    echo [LiveDanmakuOverlay] Git was not found. Skipping the update check.
    goto after_git_update
)

git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 goto after_git_update

git rev-parse --verify "@{upstream}" >nul 2>nul
if errorlevel 1 (
    echo [LiveDanmakuOverlay] This branch has no upstream. Skipping the update check.
    goto after_git_update
)

echo [LiveDanmakuOverlay] Checking for Git updates...
git fetch --quiet
if errorlevel 1 (
    echo [LiveDanmakuOverlay] Could not contact the Git remote. Using the local source.
    goto after_git_update
)

set "BEHIND_COUNT=0"
set "AHEAD_COUNT=0"
for /f "delims=" %%C in ('git rev-list --count HEAD..@{upstream} 2^>nul') do set "BEHIND_COUNT=%%C"
for /f "delims=" %%C in ('git rev-list --count @{upstream}..HEAD 2^>nul') do set "AHEAD_COUNT=%%C"

if "%BEHIND_COUNT%"=="0" goto after_git_update

if not "%AHEAD_COUNT%"=="0" (
    echo [LiveDanmakuOverlay] The local and remote branches have diverged.
    echo Resolve the Git history manually before running this script again.
    pause
    exit /b 1
)

set "WORKTREE_DIRTY="
for /f "delims=" %%S in ('git status --porcelain --untracked-files=normal 2^>nul') do set "WORKTREE_DIRTY=1"
if defined WORKTREE_DIRTY (
    echo [LiveDanmakuOverlay] A newer Git version is available, but local changes were found.
    echo Commit or stash the local changes before running this script again.
    pause
    exit /b 1
)

echo [LiveDanmakuOverlay] Updating to the latest Git version...
git pull --ff-only
if errorlevel 1 (
    echo [LiveDanmakuOverlay] Git update failed. No local files were overwritten.
    pause
    exit /b 1
)

call "%~f0" --skip-git-update
exit /b %errorlevel%

:after_git_update
tasklist /FI "IMAGENAME eq LiveDanmakuOverlay.exe" 2>nul | find /I "LiveDanmakuOverlay.exe" >nul
if not errorlevel 1 (
    echo [LiveDanmakuOverlay] The application is already running.
    exit /b 0
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [LiveDanmakuOverlay] .NET 8 was not found.
    echo Install it from: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

set "DOTNET_MAJOR="
for /f "tokens=1 delims=." %%V in ('dotnet --version 2^>nul') do set "DOTNET_MAJOR=%%V"
if not defined DOTNET_MAJOR (
    echo [LiveDanmakuOverlay] A .NET SDK is required to build the pulled source code.
    echo Install it from: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)
if %DOTNET_MAJOR% LSS 8 (
    echo [LiveDanmakuOverlay] .NET SDK 8 or newer is required.
    echo Install it from: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

dotnet --list-runtimes 2>nul | findstr /b /c:"Microsoft.WindowsDesktop.App 8." >nul
if errorlevel 1 (
    echo [LiveDanmakuOverlay] .NET 8 Desktop Runtime was not found.
    echo Install the Desktop Runtime from: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [LiveDanmakuOverlay] Building the compact version...
dotnet publish LiveDanmakuOverlay.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o publish

if errorlevel 1 (
    echo.
    echo [LiveDanmakuOverlay] Build failed. See the errors above.
    pause
    exit /b 1
)

if not exist "publish\LiveDanmakuOverlay.exe" (
    echo [LiveDanmakuOverlay] Build completed, but the executable was not found.
    pause
    exit /b 1
)

echo [LiveDanmakuOverlay] Starting...
start "" "publish\LiveDanmakuOverlay.exe"
endlocal
