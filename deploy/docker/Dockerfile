# Build from the repository root:
#   docker build -f deploy/docker/Dockerfile --target api     -t axiom-api .
#   docker build -f deploy/docker/Dockerfile --target workers -t axiom-workers .
FROM node:22-alpine AS portal
WORKDIR /portal
COPY src/Axiom.Portal/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Axiom.Portal/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props Axiom.slnx .editorconfig ./
COPY src/ src/
COPY governance/ governance/
RUN dotnet publish src/Axiom.Api/Axiom.Api.csproj -c Release -o /out/api /p:UseAppHost=false \
 && dotnet publish src/Axiom.Workers/Axiom.Workers.csproj -c Release -o /out/workers /p:UseAppHost=false

# git is required: governance and SCM sources are fetched with it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
# Demo Git repositories for local trials (deploy/local): governance + catalog, and a small code repository.
# Built as the app user because libgit2 refuses repositories owned by someone else. Harmless when unused.
USER root
COPY deploy/local/demo /opt/axiom-demo-src
RUN chown $APP_UID /opt
USER $APP_UID
RUN sh /opt/axiom-demo-src/build.sh
WORKDIR /app

FROM base AS api
COPY --from=build /out/api ./
COPY --from=portal /portal/dist ./portal
ENV Axiom__Portal__Path=/app/portal
EXPOSE 8080
ENTRYPOINT ["dotnet", "Axiom.Api.dll"]

FROM base AS workers
COPY --from=build /out/workers ./
ENTRYPOINT ["dotnet", "Axiom.Workers.dll"]
