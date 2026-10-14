@echo off
rem Same as run-foundry.cmd, but signs in to Foundry with Microsoft Entra ID (your azd/az sign-in) instead of the API
rem key: the way the Azure deployment works. Needs the Azure AI User role on the Foundry resource (deploy-azure.cmd grants it).
set Agents__Foundry__Auth=EntraId
call "%~dp0run-foundry.cmd"
