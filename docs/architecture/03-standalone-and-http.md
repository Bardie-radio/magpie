# Standalone mode and storage sinks

**Status: planned (post-MVP).** Magpie’s **commands** (search, download/cache, pause, …) are the same in Bardie and outside it. What changes is the **host** and the **storage/sink** backend.

MVP still ships **gRPC + FIFO + Kithara/orch storage** — leave seams so the modes below land without a rewrite (see [Implementation seam](#implementation-seam-leave-this-in-mvp)).

## Three ways Magpie is used

| Mode | Host | Storage / sink |
|------|------|----------------|
| **Bardie** | Kithara (uses **source module orchestrator** library) | Put/get via orchestrator → Kithara blob drivers; PCM to **session FIFO** |
| **Multi-module outside** | Ready-made **HTTP source orchestrator** (same library) | Put/get via orchestrator storage API; downloads as **finished files** the orch/module agree on |
| **Single-module outside** | None — Magpie alone | **Direct file writes** through Magpie’s **cache** pipeline (local backend instead of orch storage API); no FIFO |

Single-module mode does not invent a second download path: reuse the cache/download pipeline and **swap the storage backend** to local finished files.

## Config flips (sketch)

| Env (sketch) | Default | Effect |
|--------------|---------|--------|
| `BARDIE_STANDALONE` | unset / `false` | Off → expect orchestrator/Kithara (join + storage API + FIFO when playing into a Struna). On → no mesh join; cache uses **direct local writes** |
| `BARDIE_HTTP_ENABLED` | unset / `false` | Optional HTTP **command surface** on Magpie (useful for solo or debug). Multi-module outside users normally talk to the **HTTP source orchestrator**, not Magpie’s port |

Do **not** publish Magpie HTTP on a Bardie public edge. Day-to-day Bardie clients call Kithara REST.

## Bardie vs outside (detail)

| Concern | Behind orchestrator (Kithara or HTTP orch) | Solo standalone |
|---------|--------------------------------------------|-----------------|
| Join / Registry | Dial host / register as today | Skip |
| Control | gRPC work RPCs from orch | Magpie HTTP (or CLI) → same commands |
| Blob / cache | Orchestrator **storage API** (opaque keys) | Same cache logic → **direct local files** |
| Play into radio | PCM → session FIFO | N/A — finished files only |

Core stays one: search, resolve, ytdl, cache, decode. gRPC and HTTP are **command surfaces**; storage and audio sinks are ports.

## Implementation seam (leave this in MVP)

| Layer | Own | MVP | Later |
|-------|-----|-----|-------|
| **Commands** | `Search`, download/cache, `StartTrack`, `Stop` / `Pause` / `Resume`, … | Yes | Same |
| **Storage port** | Put/get by key (cache backend) | Kithara/orch client | + local filesystem backend for solo |
| **Audio sink** | Where PCM goes when “playing” into a host | FIFO writer | Solo skips PCM-to-FIFO; files come from storage port |
| **Command surfaces** | Transport façades | **gRPC** | + **HTTP** on Magpie; multi-module HTTP lives on the **source orch** binary |

Prefer **Command** handlers; keep storage and sink out of the gRPC service class.

Org packaging: [modules beyond Bardie](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/07-modules-beyond-bardie.md).

## Out of MVP

v0.1 is Bardie mesh only ([mvp/v0.1-scope](mvp/v0.1-scope.md)). Standalone, HTTP orch, and local cache backend are **future planned**; env names are sketches. The seam above is the MVP obligation.

**Related:** [01-role-and-boundaries.md](01-role-and-boundaries.md) · [02-contracts.md](02-contracts.md) · [operations.md](operations.md)

**Read next:** [ideas.md](ideas.md)
