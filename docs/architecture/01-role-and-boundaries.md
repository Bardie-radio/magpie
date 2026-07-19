# Role and boundaries

Magpie is the MVP **YouTube / ytdl** source. It registers with Kithara, answers search, and runs track jobs that write canonical PCM into a Struna’s **session FIFO**.

## Owns

- ytdl (or equivalent) fetch / decode of YouTube (and accepted URL/id) content
- **Cache-first** resolve against the shared library Tune + blob storage
- Creating/updating Tune metadata via Kithara when a download lands
- `Search` / `StartTrack` / `StopTrack` / `TrackStatus` behaviour for slug `magpie`
- Advertising capabilities and search field schema at `Register`
- OTLP export as `bardie.source.magpie`

## Does not own

- User DB, Struna lifecycle, FFmpeg, Stream Server (Kithara)
- Blob storage **drivers** / operator config (Kithara — Magpie uses the shared contract)
- Public HTTP edge (internal gRPC only)
- Other sources’ behaviour (Starling, Catbird)

## Surfaces

| Surface | Audience |
|---------|----------|
| gRPC to Kithara | Internal Compose network only |
| Outbound HTTPS | Media hosts via ytdl |
| Blob put/get | Shared storage (local volume / S3-compatible) |

**Read next:** [02-contracts.md](02-contracts.md)
