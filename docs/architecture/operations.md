# Operations

Env and runtime knobs for the **Magpie** container.

| Variable | Role |
|----------|------|
| `KITHARA_GRPC_ADDRESS` | Internal DNS to Kithara `:5000` (Bardie mode) |
| Join secret | Must match Kithara `BARDIE_JOIN_SECRETS` for slug `magpie` |
| `MODULE_SLUG_OVERRIDE` | Optional if community slug collides |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | External collector |
| ytdl / tool paths | Binary location, format prefs (implementation detail) |
| `BARDIE_STANDALONE` | Sketch — solo mode: skip join; cache uses **direct local file writes** instead of orch storage API. See [03-standalone-and-http](03-standalone-and-http.md) |
| `BARDIE_HTTP_ENABLED` | Sketch — optional HTTP command surface on Magpie (solo/debug). Multi-module outside → HTTP **source orchestrator**, not this port |

**Storage (Bardie mode):** do **not** configure `BARDIE_STORAGE_*` (or mounts/credentials) on Magpie. Kithara owns the backend and is Magpie’s storage **interface** (put/get by key) and/or **discovery** surface for how to reach it — one operator config on Kithara only. See [storage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/storage.md). Standalone mode uses a local output directory instead.

## Volumes / network

- Compose network reachability to Kithara (gRPC); blob IO goes through Kithara’s storage surface, not a Magpie-local driver config
- Outbound HTTPS for media hosts (ytdl)
- Bardie mode: no public ports — gRPC only on the Compose network
- Standalone + HTTP: operator may publish Magpie’s HTTP port; not part of Kithara’s URI map

## Observability

- `service.name=bardie.source.magpie`
- Propagate `traceparent` on all RPCs from Kithara

**Related:** Kithara [configuration](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/operations/configuration.md) · [observability](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/operations/observability.md) · [storage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/storage.md)

**Read next:** [ideas.md](ideas.md)
