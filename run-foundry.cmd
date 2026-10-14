@echo off
rem WhyLine with Microsoft Foundry agents: build, test and run. Output goes to the logs folder.
cd /d "%~dp0"
if not exist logs mkdir logs
echo Stopping any running WhyLine...
taskkill /IM WhyLine.Web.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
echo Building...
dotnet build WhyLine.slnx > logs\build.txt 2>&1
echo %ERRORLEVEL% > logs\build.exit
echo Testing...
dotnet test tests\WhyLine.Tests\WhyLine.Tests.csproj --no-build > logs\test.txt 2>&1
echo %ERRORLEVEL% > logs\test.exit
echo Starting WhyLine with Foundry agents on http://localhost:5080 (close this window to stop)...
dotnet run --project src\WhyLine.Web\WhyLine.Web.csproj --no-build --launch-profile "WhyLine (Foundry agents)" > logs\run.txt 2>&1
