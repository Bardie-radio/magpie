# Contracts

Magpie speaks the Kithara **source module** gRPC contract — sketch and invariants: [grpc-source-module](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-source-module.md).

## Registration

On startup: `Register` with slug `magpie` (or `MODULE_SLUG_OVERRIDE`), **join secret**, capabilities `search` | `play` | `pause`, gRPC advertise address, and **search field schema** (`title` mandatory; encourage `artist`, `owner`).

## Search

| Client mode | Magpie behaviour |
|-------------|------------------|
| Quicksearch | Plain-text / title-only; fan-out when Kithara omits module |
| Regular search | Structured fields from advertised schema |

**Plain-text fallback:** if text search is empty, try treating the query as a **native id or YouTube URL** before returning empty.

## Play / track jobs

1. Resolve track ref (Tune id, search-result ref, video id, or YouTube URL).
2. **Cache hit** — Tune exists and blob is present → open blob, decode to canonical PCM (`s16le` / 48 kHz / stereo MVP), write to `fifo_path`.
3. **Cache miss** — ytdl download → **put** blob via storage contract → create/update Tune (metadata + storage key) → decode to FIFO.
4. Honor `StopTrack` promptly; on Struna teardown Kithara stops the track job **before** killing FFmpeg/FIFO ([source-instances](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/source-instances.md)).

`pause` capability: freeze/resume the track job without tearing it down (unlike Starling).

## Storage

Opaque **storage keys** on Tunes — not host paths. Driver and path/S3 config live only on Kithara; Magpie put/gets through Kithara’s storage interface/discovery (no Magpie `BARDIE_STORAGE_*`). See [storage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/storage.md).

**Read next:** [mvp/v0.1-scope.md](mvp/v0.1-scope.md)
