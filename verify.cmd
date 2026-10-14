@echo off
rem WhyLine: build, test and run. Output goes to the logs folder.
cd /d "%~dp0"
if not exist logs mkdir logs
echo Stopping any running WhyLine...
taskkill /IM WhyLine.Web.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
echo Checking .NET SDKs...
dotnet --list-sdks > logs\sdks.txt 2>&1
echo Building...
dotnet build WhyLine.slnx > logs\build.txt 2>&1
echo %ERRORLEVEL% > logs\build.exit
echo Testing...
dotnet test tests\WhyLine.Tests\WhyLine.Tests.csproj --no-build > logs\test.txt 2>&1
echo %ERRORLEVEL% > logs\test.exit
echo Starting WhyLine on http://localhost:5080 (close this window to stop)...
dotnet run --project src\WhyLine.Web\WhyLine.Web.csproj --no-build --launch-profile "WhyLine (Template agents)" > logs\run.txt 2>&1
