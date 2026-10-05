# Deploying Axiom

Images (build from the repository root; the `api` image embeds the portal):

    docker build -f deploy/docker/Dockerfile --target api     -t <registry>/axiom-api:0.1.0 .
    docker build -f deploy/docker/Dockerfile --target workers -t <registry>/axiom-workers:0.1.0 .

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

## Kustomize

    kubectl kustomize deploy/kustomize/overlays/dev  | kubectl apply -f -   # evaluation
    kubectl kustomize deploy/kustomize/overlays/prod | kubectl apply -f -   # edit hosts/images/IdP first

Delete the previous migration Job before re-applying: `kubectl -n axiom delete job axiom-migrate --ignore-not-found`.

## Identity provider (production)

Register a **public** client (authorization code + PKCE, no secret) with redirect URI
`https://<host>/` and allow CORS for that origin on the authority's discovery and token endpoints.
The access token must carry `org` and either a `roles` claim (Reader, Contributor, Approver, ...) or the
`axiom.*` scopes; Axiom rejects tokens without an organization.
