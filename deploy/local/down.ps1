<#
.SYNOPSIS
  Removes the local Axiom deployment (namespace axiom, including the Postgres volume) from Docker Desktop.
#>
$ErrorActionPreference = 'Stop'
$context = kubectl config current-context
if ($context -ne 'docker-desktop') { throw "kubectl context is '$context', not docker-desktop; refusing to delete." }
kubectl delete namespace axiom --ignore-not-found
