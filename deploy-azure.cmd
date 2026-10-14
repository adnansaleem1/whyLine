@echo off
rem Deploys WhyLine to Azure Container Apps with the Azure Developer CLI (azd), using your existing Foundry project.
rem The app signs in to Foundry with a managed identity: no API key goes to the cloud. See docs\deploy-azure.md.
setlocal
cd /d "%~dp0"
if not exist logs mkdir logs

where azd >nul 2>&1
if errorlevel 1 (
  echo The Azure Developer CLI is not installed. Install it with:  winget install microsoft.azd
  echo then open a new window and run this script again.
  pause & exit /b 1
)

echo Checking Azure sign-in...
azd auth login --check-status >nul 2>&1 || azd auth login
if errorlevel 1 ( echo Sign-in failed. & pause & exit /b 1 )

rem Environment: created once, then reused. azd asks for the subscription and region the first time.
azd env select whyline >nul 2>&1 || azd env new whyline
if errorlevel 1 ( echo Could not create the azd environment. & pause & exit /b 1 )

rem Foundry settings come from your .NET user secrets (whyline-web-7d3f). The API key there is NOT used or uploaded.
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$s=Join-Path $env:APPDATA 'Microsoft\UserSecrets\whyline-web-7d3f\secrets.json'; if (Test-Path $s) { (Get-Content $s -Raw | ConvertFrom-Json).'Agents:Foundry:ProjectEndpoint' }"`) do set FPE=%%v
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$s=Join-Path $env:APPDATA 'Microsoft\UserSecrets\whyline-web-7d3f\secrets.json'; if (Test-Path $s) { (Get-Content $s -Raw | ConvertFrom-Json).'Agents:Foundry:ReasoningModel' }"`) do set FRM=%%v
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$s=Join-Path $env:APPDATA 'Microsoft\UserSecrets\whyline-web-7d3f\secrets.json'; if (Test-Path $s) { (Get-Content $s -Raw | ConvertFrom-Json).'Agents:Foundry:FastModel' }"`) do set FFM=%%v
if "%FPE%"=="" set /p FPE=Foundry project endpoint (https://...services.ai.azure.com/api/projects/...): 
azd env set FOUNDRY_PROJECT_ENDPOINT "%FPE%" >nul
if not "%FRM%"=="" azd env set FOUNDRY_REASONING_MODEL "%FRM%" >nul
if not "%FFM%"=="" azd env set FOUNDRY_FAST_MODEL "%FFM%" >nul

set FRG=
for /f "usebackq delims=" %%v in (`azd env get-value FOUNDRY_RESOURCE_GROUP 2^>nul`) do set FRG=%%v
echo %FRG% | findstr /c:"ERROR" >nul && set FRG=
if "%FRG%"=="" set /p FRG=Resource group of your Foundry resource (Azure portal, Overview page): 
azd env set FOUNDRY_RESOURCE_GROUP "%FRG%" >nul

echo.
echo Deploying to Azure. The first run asks for a subscription and region, then takes about 10 minutes.
azd up
echo %ERRORLEVEL% > logs\deploy.exit
echo.
azd env get-value WHYLINE_URL > logs\deploy-url.txt 2>nul
for /f "usebackq delims=" %%v in ("logs\deploy-url.txt") do echo WhyLine is live at %%v
pause
