@echo off
rem WhyLine evaluation: plays every fixture through the Foundry newsroom and scores the published cards.
rem Results: eval\summary.md, eval\results.json and eval\report.html. Console output: logs\eval.txt.
rem Extra arguments are passed through, e.g.  run-eval.cmd --matches comeback --moments 2   or   run-eval.cmd --safety
cd /d "%~dp0"
if not exist logs mkdir logs
echo Evaluating (this calls your Foundry models; a full run takes several minutes)...
dotnet run --project tools\WhyLine.Evaluate\WhyLine.Evaluate.csproj -- %* > logs\eval.txt 2>&1
echo %ERRORLEVEL% > logs\eval.exit
echo Building the HTML report...
if not exist dotnet-tools.json if not exist .config\dotnet-tools.json dotnet new tool-manifest >nul 2>&1
dotnet tool install Microsoft.Extensions.AI.Evaluation.Console --prerelease >> logs\eval.txt 2>&1
dotnet tool run aieval report --path eval\store --output eval\report.html >> logs\eval.txt 2>&1
echo Done. Open eval\summary.md or eval\report.html.
