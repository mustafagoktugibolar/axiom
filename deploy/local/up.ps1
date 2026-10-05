<#
.SYNOPSIS
  Builds Axiom and runs it on the Docker Desktop Kubernetes cluster with demo data.
.DESCRIPTION
  Prerequisites: Docker Desktop with Kubernetes enabled, kubectl. Re-run any time to rebuild and redeploy.
  Opens nothing destructive: it only touches the `axiom` namespace of the current kubectl context.
  -SkipBuild reuses the axiom-api:local / axiom-workers:local images you already built.
  -Port is the local port for the port-forward (default 8080).
#>
param(
  [switch]$SkipBuild,
  [int]$Port = 8080
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root

$context = kubectl config current-context
if ($context -ne 'docker-desktop') {
  throw "kubectl context is '$context'. Switch to docker-desktop (kubectl config use-context docker-desktop) or enable Kubernetes in Docker Desktop."
}

if (-not $SkipBuild) {
  Write-Host '==> Building images (first run takes a few minutes)'
  docker build -f deploy/docker/Dockerfile --target api -t axiom-api:local .
  if ($LASTEXITCODE) { throw 'api image build failed' }
  docker build -f deploy/docker/Dockerfile --target workers -t axiom-workers:local .
  if ($LASTEXITCODE) { throw 'workers image build failed' }
}

Write-Host '==> Deploying to namespace axiom'
# A Job pod template is immutable; remove the previous migration run before re-applying.
kubectl -n axiom delete job axiom-migrate --ignore-not-found | Out-Null
kubectl apply -k deploy/kustomize/overlays/local
if ($LASTEXITCODE) { throw 'kubectl apply failed' }

Write-Host '==> Waiting for the database, migration and API'
kubectl -n axiom rollout status statefulset/axiom-postgres --timeout=180s
kubectl -n axiom wait --for=condition=complete job/axiom-migrate --timeout=240s
kubectl -n axiom rollout status deploy/axiom-api --timeout=180s
kubectl -n axiom rollout status deploy/axiom-workers --timeout=180s

Write-Host ''
Write-Host "==> Ready: http://localhost:$Port  (click 'Sign in', then follow 'Get started')"
Write-Host '    Port-forward runs in this window; Ctrl+C stops it (Axiom keeps running in the cluster).'
Write-Host '    Remove everything with deploy/local/down.ps1'
kubectl -n axiom port-forward svc/axiom $Port`:80
