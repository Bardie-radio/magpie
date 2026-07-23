# Magpie

YouTube / ytdl **source module** for Bardie — search, cache-first play, PCM into Kithara’s session FIFO.

| | |
|--|--|
| **Status** | **MVP** v0.1 — Magpie app live (YoutubeExplode + FFmpeg.AutoGen). `sine` FIFO proof via optional [`Bardie.Module.Source.Debug`](https://github.com/Bardie-radio/kithara/tree/main/libs/Bardie.Module.Source.Debug) (Debug builds only). |
| **Image / Compose** | `magpie` |
| **OTel** | `bardie.source.magpie` |
| **Slug** | `magpie` |
| **Capabilities** | `search`, `play`, `pause` |

Architecture: [docs/architecture](docs/architecture/README.md).

Kithara contracts: [grpc-source-module](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-source-module.md) · [grpc-blob-storage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-blob-storage.md) · [grpc-library](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/interfaces/grpc-library.md) · [source-modules](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/source-modules.md) · [storage](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/storage.md) · [library-and-tunes](https://github.com/Bardie-radio/kithara/blob/main/docs/architecture/domains/library-and-tunes.md)

Org: [Bardie architecture](https://github.com/Bardie-radio/.github/tree/main/profile/docs/architecture)
