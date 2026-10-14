# Deploy to Azure

WhyLine deploys with the Azure Developer CLI (`azd`) to **Azure Container Apps**, next to your existing **Microsoft
Foundry** project. The app signs in to Foundry with a **managed identity** (Microsoft Entra ID), so no API key is
stored in the cloud, and its OpenTelemetry traces go to **Application Insights**, connected to the Foundry project's
Tracing view.

## What gets created

| Resource | Purpose |
|---|---|
| Resource group `rg-<env>` | Everything below |
| Container Apps environment + app | WhyLine, one always-on replica (0.5 vCPU, 1 GiB). The live match runs in memory, so it must be exactly one instance |
| Container Registry (Basic) | The image, built in Azure (`remoteBuild`), so Docker isn't needed locally |
| User-assigned managed identity | Pulls the image and calls Foundry |
| Log Analytics + Application Insights | Logs, traces and metrics |
| In the Foundry resource's group | **Azure AI User** and **Cognitive Services OpenAI User** for the app's identity; **Azure AI User** for you (so you can run locally with Entra ID); an Application Insights connection on the Foundry project (Foundry → Tracing) |

Templates: `azure.yaml`, `infra/main.bicep`, `infra/modules/app.bicep`, `infra/modules/foundry-access.bicep`.

## Before you start

- The Azure Developer CLI: `winget install microsoft.azd`
- Rights to create resources in a subscription, and to assign roles on the Foundry resource (Owner or User Access
  Administrator on its resource group).
- The resource group your Foundry resource lives in (Azure portal → your Foundry resource → Overview).

## Deploy

Double-click **`deploy-azure.cmd`**. It:

1. Signs you in to Azure (`azd auth login`) if needed.
2. Creates the `whyline` azd environment. The first time, azd asks for a subscription and a region.
3. Reads the Foundry project endpoint from your .NET user secrets (see the README). The API key there is not used and
   never leaves your machine; `.dockerignore` also keeps local settings files out of the image.
4. Asks once for the Foundry resource group.
5. Runs `azd up`: provisions the resources, builds the image in the registry and deploys it. The URL is printed at the
   end and saved to `logs/deploy-url.txt`.

The same by hand:

```bash
azd auth login
azd env new whyline
azd env set FOUNDRY_PROJECT_ENDPOINT "https://<resource>.services.ai.azure.com/api/projects/<project>"
azd env set FOUNDRY_RESOURCE_GROUP "<resource group of the Foundry resource>"
azd env set FOUNDRY_REASONING_MODEL gpt-5.5        # optional, these are the defaults
azd env set FOUNDRY_FAST_MODEL gpt-5.4-mini
azd up
```

After a code change, `azd deploy` ships a new image. `azd down --purge` deletes everything WhyLine created (the Foundry
resource and its models are not touched, but the role assignments and the tracing connection on it should be removed by
hand if you want them gone).

## Check it

- Open the URL: the trace panel header should say **Microsoft Foundry agents · Agent Framework workflow**.
- `GET /health` returns `{"status":"ok","agents":"Foundry"}`.
- Container logs (portal → the container app → Log stream) start with
  `WhyLine started. Agents: Foundry (Microsoft Entra ID). Orchestration: Workflow. Telemetry: Application Insights`.
- Foundry portal → your project → **Tracing**: one trace per moment within a few minutes.

## Entra ID locally

`run-foundry-entra.cmd` runs the app on your machine the way the cloud does: Entra ID (your `azd`/`az` sign-in) instead
of the API key. The deployment grants you the role it needs; role assignments can take a few minutes to apply.

## Cost

The app is always on (one replica), so it bills continuously while deployed. As a rough guide it is a few tens of US
dollars a month plus the registry and log ingestion; check the Azure pricing calculator for your region. Model calls are
billed on the Foundry resource as usual. Run `azd down --purge` after judging.
