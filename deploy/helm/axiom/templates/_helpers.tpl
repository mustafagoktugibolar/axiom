{{- define "axiom.name" -}}{{ .Chart.Name }}{{- end -}}
{{- define "axiom.fullname" -}}{{ printf "%s-%s" .Release.Name .Chart.Name | trunc 50 | trimSuffix "-" }}{{- end -}}
{{- define "axiom.labels" -}}
app.kubernetes.io/name: {{ include "axiom.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}
{{- define "axiom.selector" -}}
app.kubernetes.io/name: {{ include "axiom.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
{{- define "axiom.image" -}}
{{- $reg := .root.Values.image.registry -}}
{{- $tag := default .root.Chart.AppVersion .root.Values.image.tag -}}
{{- if $reg }}{{ printf "%s/%s:%s" (trimSuffix "/" $reg) .name $tag }}{{ else }}{{ printf "%s:%s" .name $tag }}{{ end -}}
{{- end -}}
{{- define "axiom.secretName" -}}{{ default (printf "%s-secrets" (include "axiom.fullname" .)) .Values.secrets.existingSecret }}{{- end -}}
{{- define "axiom.connectionString" -}}
{{- if .Values.database.embedded.enabled -}}
{{- printf "Host=%s-postgres;Port=5432;Username=axiom;Password=%s;Database=axiom" (include "axiom.fullname" .) (required "database.embedded.password is required" .Values.database.embedded.password) -}}
{{- else -}}
{{- required "database.connectionString is required (or enable database.embedded, or set secrets.existingSecret)" .Values.database.connectionString -}}
{{- end -}}
{{- end -}}
{{/* Environment shared by api, workers and the migration job. */}}
{{- define "axiom.envItems" -}}
- name: ConnectionStrings__Axiom
  valueFrom: { secretKeyRef: { name: {{ include "axiom.secretName" . }}, key: connection-string } }
{{- if .Values.auth.devMode.enabled }}
- name: ASPNETCORE_ENVIRONMENT
  value: Development
- name: Axiom__Auth__DevelopmentSigningKey
  valueFrom: { secretKeyRef: { name: {{ include "axiom.secretName" . }}, key: dev-signing-key } }
{{- end }}
{{- end -}}
{{- define "axiom.env" -}}
env:
  {{- include "axiom.envItems" . | nindent 2 }}
envFrom:
  - configMapRef: { name: {{ include "axiom.fullname" . }} }
{{- end -}}
{{/* Workers additionally receive the Git host tokens used to fetch governance sources. */}}
{{- define "axiom.workerEnv" -}}
env:
  {{- include "axiom.envItems" . | nindent 2 }}
  {{- range .Values.governance.credentials }}
  - name: {{ printf "Axiom__Governance__Credentials__%s__Token" .host }}
    valueFrom: { secretKeyRef: { name: {{ include "axiom.secretName" $ }}, key: {{ .secretKey | quote }} } }
  {{- end }}
envFrom:
  - configMapRef: { name: {{ include "axiom.fullname" . }} }
{{- end -}}
