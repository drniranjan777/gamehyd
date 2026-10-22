# Live-ops calendar (first 12 weeks after launch)

Status: **proposal** for the product owner and economy operator. Dates are relative to wave 3 (full India launch, week 0). Every item uses tools that exist:

- **Seasons:** Admin portal → Seasons. Draft, then publish; published seasons never overlap.
- **Store products:** Catalogue. A second person approves.
- **Events and news:** Live ops. They show on the website.
- **Kill switches:** Live ops.

| Week | Content                                                              | Economy                                                 | Operations                                    |
| ---- | -------------------------------------------------------------------- | ------------------------------------------------------- | --------------------------------------------- |
| 0    | Launch event "Grand Opening" (news, website)                         | Launch catalogue as published in migration 0007         | Launch stand-ups daily; release waves         |
| 1    | **Season 1** starts (8 weeks): 30 tiers, free track + pass           | Season pass product (INR) with SEASON_PASS grant        | First weekly economy review                   |
| 2    | Weekend event: double XP (proposal, needs an XP multiplier setting)  | —                                                       | Capacity check against the plan               |
| 3    | New cosmetic drop (2 skins)                                          | Review coin sink ratio; adjust prices of new items only | First on-call retrospective                   |
| 4    | Community event: report-a-bug week                                   | —                                                       | Mid-season balance review (TTK table)         |
| 5    | Festival theme cosmetics (Diwali-timed if the calendar fits)         | Limited-time products (availableFrom / availableUntil)  | —                                             |
| 6    | Balance patch if needed (new weapon balance version)                 | —                                                       | Release wave for the client patch             |
| 8    | Season 1 ends; **Season 2** starts the same day                      | Season 1 claim window closes at its end time            | Season retrospective: claims, pass conversion |
| 9–12 | Season 2 content; evaluate 50-player mode, squads, parties from data | Quarterly economy review                                | Cost review against the capacity plan         |

## Rules

- A season or product is published at least 48 hours before it starts, so a second person can check it.
- Prices never change after publishing; a "sale" is a new product with its own dates.
- No item may change gameplay (BRD §1, §12); cosmetics with stats are rejected by the database.
- Every week: Beta metrics, Operations and Economy dashboards reviewed and summarised in the live-ops channel.

## Not built yet (needed by this calendar)

- An XP multiplier setting for double-XP events (small: a live-config field read by the results service).
- In-game news (events show on the website only).
