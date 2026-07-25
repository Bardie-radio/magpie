# Contracts

Magpie speaks the Kithara **source module** gRPC contract — **v0.1 draft**: [grpc-source-module](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-source-module.md). Blob put/get: [grpc-blob-storage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-blob-storage.md). Tune upsert: [grpc-library](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-library.md) (`EnsureTune`).

## Internal shape (MVP seam)

Implement work verbs as **commands** (handlers) behind a small interface. The gRPC server is a **command surface** (façade) — not where ytdl / decode / sink logic lives. Keep the **storage port** (orch/Kithara API vs local cache files) and **audio sink** (FIFO vs N/A) behind interfaces so multi-module HTTP orch and solo direct-file mode can land later. See [03-standalone-and-http](03-standalone-and-http.md) (planned).

## Registration

On startup: Module Registry `Register` (dial Kithara) with slug `magpie` (or `MODULE_SLUG_OVERRIDE`), **join secret**, capabilities `search` | `play` | `pause` | `prefetch`, gRPC advertise address, and **search field schema** (`title` mandatory; encourage `artist`, `owner`).

## Search

| Client mode | Magpie behaviour |
|-------------|------------------|
| Quicksearch | Plain-text / title-only; fan-out when Kithara omits module |
| Regular search | Structured fields from advertised schema |

**Plain-text fallback:** if text search is empty, try treating the query as a **native id or YouTube URL** before returning empty.

## Play / track jobs

1. Resolve track ref (Tune id, search-result ref, video id, or YouTube URL).
2. **Cache hit** — Tune exists and blob is present → open blob via Kithara storage API, decode to canonical PCM (`CanonicalPcm` / s16le / 48 kHz / stereo MVP), write to session audio endpoint.
3. **Cache miss** — download → **Put** blob via Kithara [BlobStorage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-blob-storage.md) (`tunes/magpie/…`) → [EnsureTune](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-library.md) (metadata + storage key) → decode to endpoint.
4. Honor `StopTrack` / `PauseTrack` / `ResumeTrack`; on Struna teardown Kithara stops the track job **before** killing FFmpeg/endpoint ([source-instances](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/source-instances.md)).

`pause` capability: freeze/resume the track job without tearing it down (unlike Starling).

`prefetch` capability: warm blob cache on enqueue via `PrefetchTrack` (no FIFO write); `StartTrack` still owns session PCM.

## Storage

Opaque **storage keys** on Tunes — not host paths. Layout: `tunes/magpie/…`. Driver and path/S3 config live only on Kithara; Magpie put/gets through Kithara’s [BlobStorage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-blob-storage.md) (no Magpie `BARDIE_STORAGE_*`). See [storage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/storage.md).

**Read next:** [mvp/v0.1-scope.md](mvp/v0.1-scope.md)
