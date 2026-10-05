# Deploying Axiom

Images (build from the repository root; the `api` image embeds the portal):

    docker build -f deploy/docker/Dockerfile --target api     -t <registry>/axiom-api:0.1.0 .
    docker build -f deploy/docker/Dockerfile --target workers -t <registry>/axiom-workers:0.1.0 .

## Local (Docker Desktop Kubernetes)

    powershell -File deploy/local/up.ps1      # builds both images, deploys with demo data, port-forwards
    # open http://localhost:8080 -> Sign in -> "Get started"
    powershell -File deploy/local/down.ps1    # removes the axiom namespace (and the database volume)

Needs Docker Desktop with Kubernetes enabled and `kubectl`; no Helm. It uses `deploy/kustomize/overlays/local`:
in-cluster Postgres (pgvector image), the built-in dev login, and the demo governance repository
(`deploy/local/demo-repo`, baked into the workers image) registered as organization `dev`. Re-run `up.ps1`
after code changes; `-SkipBuild` redeploys without rebuilding.

## Helm

Local evaluation (embedded Postgres, built-in dev login; never on a shared cluster):

    helm install axiom deploy/helm/axiom -n axiom --create-namespace \
      --set image.registry=<registry> \
      --set auth.devMode.enabled=true --set auth.devMode.signingKey=<32+ random chars> \
      --set database.embedded.enabled=true --set database.embedded.password=<password>
    kubectl -n axiom port-forward svc/axiom-axiom 8080:80     # open http://localhost:8080, click "Sign in"

Production (OIDC, external Postgres):

    helm upgrade --install axiom deploy/helm/axiom -n axiom --create-namespace \
      --set image.registry=<registry> \
      --set auth.authority=https://idp.example.com/realms/axiom --set auth.portalClientId=axiom-portal \
      --set publicBaseUrl=https://axiom.example.com \
      --set secrets.existingSecret=axiom-secrets \
      --set ingress.enabled=true --set ingress.host=axiom.example.com

The existing Secret needs the key `connection-string`. Migrations run as a Helm hook Job
(`post-install,pre-upgrade`) using the API image with `--migrate`.

## Governance source (seed and sync)

The workers register the configured governance repositories at startup and then re-sync every
`governance.sync.interval` (default one minute); a rejected commit leaves the previous snapshot in force.
Without a source the registry stays empty.

    # values.yaml (or --set)
    governance:
      sources:
        - { organizationId: acme, repositoryUrl: https://github.com/acme/governance.git, branch: main, rootPath: "" }
      credentials:                       # private repositories only; one entry per Git host
        - { host: github.com, secretKey: git-token-github }   # key in the Secret; add `token:` to have Helm render it

`organizationId` must equal the `org` claim of the tokens that read it. Kustomize: set
`Axiom__Workers__GovernanceSync__Sources__0__*` in the overlay's ConfigMap patch and add `git-token-github`
to the Secret. Locally the same keys work as environment variables for `dotnet run --project src/Axiom.Workers`
(plus `Axiom__Governance__AllowLocalRepositories=true` for a path or `file://` URL).

## Kustomize

    kubectl kustomize deploy/kustomize/overlays/dev  | kubectl apply -f -   # evaluation
    kubectl kustomize deploy/kustomize/overlays/prod | kubectl apply -f -   # edit hosts/images/IdP first

Delete the previous migration Job before re-applying: `kubectl -n axiom delete job axiom-migrate --ignore-not-found`.

## Identity provider (production)

Register a **public** client (authorization code + PKCE, no secret) with redirect URI
`https://<host>/` and allow CORS for that origin on the authority's discovery and token endpoints.
The access token must carry `org` and either a `roles` claim (Reader, Contributor, Approver, ...) or the
`axiom.*` scopes; Axiom rejects tokens without an organization.
