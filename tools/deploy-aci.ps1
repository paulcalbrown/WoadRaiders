<#
.SYNOPSIS
Deploys (or updates) the WoadRaiders dedicated server on Azure Container
Instances. Run by CI after every published release, and runnable locally with
an az login. `az container create` on an existing group is a full replace, so
changing the image tag redeploys the server.

  .\tools\deploy-aci.ps1 -Tag v13

Costs money while running (~$39/month for 1 vCPU / 1 GB always-on, billed
per second): `az container stop -g woadraiders -n woadraiders-server` when
idle, `az container start ...` to bring it back.
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$ResourceGroup = 'woadraiders',
    [string]$Name = 'woadraiders-server',
    [string]$DnsLabel = 'woadraiders',
    [string]$Location = 'eastus'
)

$ErrorActionPreference = 'Stop'
$image = "ghcr.io/paulcalbrown/woadraiders-server:$Tag"

Write-Host "Deploying $image to ACI $ResourceGroup/$Name ..."
# The ghcr package must be PUBLIC for this anonymous pull; the game's binaries
# are public release assets anyway. LiteNetLib is UDP — hence udp/9050 and why
# this is ACI, not Container Apps (no UDP ingress there).
az container create `
    --resource-group $ResourceGroup `
    --name $Name `
    --location $Location `
    --image $image `
    --os-type Linux `
    --cpu 1 `
    --memory 1 `
    --restart-policy Always `
    --ports 9050 `
    --protocol UDP `
    --ip-address Public `
    --dns-name-label $DnsLabel `
    --output none
if ($LASTEXITCODE -ne 0) { throw "az container create failed with exit code $LASTEXITCODE" }

$fqdn = az container show --resource-group $ResourceGroup --name $Name --query "ipAddress.fqdn" -o tsv
$state = az container show --resource-group $ResourceGroup --name $Name --query "instanceView.state" -o tsv
Write-Host "Deployed: $fqdn (udp/9050), state: $state"
