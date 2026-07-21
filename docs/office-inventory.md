# Microsoft Office inventory

The endpoint service exposes the read-only `software.office_inventory` RPC command.
It reports the overall Office version and architecture, Click-to-Run configuration,
registered Office products, and these installed desktop applications:

- Word
- Excel
- PowerPoint
- Outlook
- OneNote
- Access
- Publisher
- Visio
- Project

Each detected application includes its executable path, file version, and product
version. The collector does not read product keys, activation tokens, credentials,
or user documents, and it does not launch or modify Office.

## Run it locally

Start `ControlPlane.Api` and `EndpointAgents.Service`, then run:

```powershell
$agents = Invoke-RestMethod http://localhost:5052/api/agents
$agentId = $agents[0].agentId
$body = @{
    jsonRpc = "2.0"
    id = "office-inventory-1"
    method = "software.office_inventory"
    params = $null
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Post `
    -Uri "http://localhost:5052/api/agents/$agentId/commands" `
    -ContentType "application/json" `
    -Body $body | ConvertTo-Json -Depth 12
```

The command is also available from the web dashboard command menu.
