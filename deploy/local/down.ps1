<#
.SYNOPSIS
  Removes the local Axiom deployment (namespaces axiom, including the Postgres volume, and observability) from Docker Desktop.
#>
$ErrorActionPreference = 'Stop'
$context = kubectl config current-context
if ($context -ne 'docker-desktop') { throw "kubectl context is '$context', not docker-desktop; refusing to delete." }
kubectl delete namespace axiom --ignore-not-found
kubectl delete namespace observability --ignore-not-found
