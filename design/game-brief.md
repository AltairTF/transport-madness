# Game Brief: Transport Madness

<!-- Distilled from docs/game-plan.md, docs/campaign-plan.md and docs/project-status.md (2026-10-24). Those stay the detailed record. -->

**One-sentence pitch:** A 3D transport management game with a living lane-level traffic simulation, set as a regional campaign in a fictional country at war from January 1938 to December 1945.

## Core loop
- Build road and rail networks (cut and fill, bridges, tunnels) between towns and industries.
- Run stations, lines and vehicles; move cargo and people along the industry chains.
- Earn from deliveries, repair and maintain the fleet, and meet government orders within the budget.
- Towns grow only when supplied and connected, which opens more of the map to serve.

## Player goal & fail state — what "working" looks like
- Goal: keep the region's war and civil industry supplied through 1945 with a good standing.
- Fail: deficit and fines lead to dismissal. A session feels complete when a profitable network keeps its towns growing and its orders met.

## MVP — what must exist to be the game
- Dynamic lane traffic (overtaking, queues, level crossings, signals), deterministic and seeded.
- Procedural terrain, towns and industries; road and rail building.
- Stations, lines, bus, truck and train, with company money and upkeep.
- Cargo chains, trip flows and town growth.
- Incidents, maintenance and depots.
- Campaign: budget, orders, rationing, call-up, requisition.
- Save and load, overlays, interface.

## Out of scope — not building this
- Multiplayer.
- Manual terraforming.
- Tides and boats.
- Real nations or brands; no graphic content.
- Undo of construction (a mistake is pulled down).

## Build order
1. M0–M5 sim: traffic, terrain, economy, towns, rail, maintenance (done).
2. Campaign C1–C15 (done).
3. M6 interface and save/load, M7 art (done); R1–R4 world revamp (done).
4. Open: play-test the 3D look, R5 organic growth, balance of the new land, audio, M8 period art.

---
**Who it's for / what they feel:** A network builder who watches traffic and towns come alive; serious about the period, light in how it plays.

**Art & audio direction:** Stylized PS3-era 3D with a free orbit camera; audio not yet decided.

**Reference game:** Transport Fever, keeping the lane-level living traffic and town growth, plus a Transport Tycoon style chain economy.
