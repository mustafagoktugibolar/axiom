#!/bin/sh
# Builds the two demo Git repositories used by deploy/local:
#   /opt/axiom-demo          governance records + catalog manifest (branch main)
#   /opt/axiom-demo-gateway  a small code repository: main, plus branch feature/role-routing whose single
#                            commit adds a database import to the gateway (what ARCH-001 forbids), plus branch
#                            feature/role-lookup that does the same without the import (only semantic analysis sees it)
set -e
SRC=$(dirname "$0")
GIT="git -c user.name=axiom-demo -c user.email=demo@axiom.local"

rm -rf /opt/axiom-demo /opt/axiom-demo-gateway
cp -r "$SRC/governance" /opt/axiom-demo
(cd /opt/axiom-demo && git init -q -b main && git add -A && $GIT commit -q -m "Demo governance data")

mkdir -p /opt/axiom-demo-gateway
cp -r "$SRC/gateway/base/." /opt/axiom-demo-gateway/
cd /opt/axiom-demo-gateway
git init -q -b main && git add -A && $GIT commit -q -m "Initial gateway"
git checkout -q -b feature/role-routing
cp -r "$SRC/gateway/feature/." .
git add -A && $GIT commit -q -m "Route landing page by user role"
git checkout -q main

# Second branch: no forbidden import, so every deterministic rule passes. Only the semantic analyzer
# notices that it still does what ARCH-001 forbids (database access for routing, from the Gateway).
git checkout -q -b feature/role-lookup
cp -r "$SRC/gateway/semantic/." .
git add -A && $GIT commit -q -m "Look up the landing page per request"
git checkout -q main
