# Module maturity

SetNet packages are not all the same kind of promise. Use this status when choosing what to ship.

| Status | Meaning |
|---|---|
| Stable | Core or companion package intended for normal application use. Public API should evolve compatibly. |
| Preview | Usable, tested, and packaged, but API shape may still change as real integrations harden it. |
| Reference | Working gameplay/domain implementation meant as a starting point. Expect to adapt persistence, policy, and edge-case semantics for your game. |
| Experimental | Useful for prototypes or narrow environments; validate carefully before production. |

Current recommended reading:

- Stable: `SetNet`, serializers, `SetNet.Rpc`, `SetNet.Auth`, `SetNet.Rooms`, `SetNet.RateLimit`, `SetNet.WebSockets`, `SetNet.InMemory`, logging adapters.
- Preview: `SetNet.StateSync`, `SetNet.Matchmaking`, `SetNet.Streams`, `SetNet.Fragmentation`, `SetNet.Relay`, `SetNet.NatPunch`, infrastructure packages.
- Reference: RPG/economy/gameplay modules such as Inventory, Trade, Mail, Wallet, Vendor, Auction, Crafting, Loot, Quests, Guilds, Mobs, NPC, Dialogue, Combat, Abilities.
- Experimental: engine bindings and network-environment helpers whose behaviour depends heavily on host runtime, platform, firewall/NAT, or engine version.

When in doubt, treat non-core gameplay modules as server-authoritative reference implementations: good enough to learn and extend, not a substitute for your product's persistence, moderation, fraud, economy, and abuse policy.
