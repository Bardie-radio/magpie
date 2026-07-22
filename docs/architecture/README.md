# Magpie architecture

How Magpie implements Bardie’s source-module contract. Shared library and storage *models* live in Kithara; Magpie owns ytdl, cache-first resolve, and PCM write. Magpie may also run **outside** Bardie as a download tool ([03-standalone-and-http](03-standalone-and-http.md)) — Kithara does not define that mode.

**Stage:** Implementation underway for Kithara Phase 3 — see [mvp/v0.1-scope.md](mvp/v0.1-scope.md).

## Read order

1. [01-role-and-boundaries.md](01-role-and-boundaries.md)
2. [02-contracts.md](02-contracts.md)
3. [mvp/v0.1-scope.md](mvp/v0.1-scope.md)
4. [operations.md](operations.md)
5. [03-standalone-and-http.md](03-standalone-and-http.md) — optional; outside Bardie
6. [ideas.md](ideas.md)

**Related:** [../README.md](../README.md)
