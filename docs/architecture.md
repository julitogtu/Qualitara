# Relay Pulse — architecture

This describes the code as built. `PLAN.md` is rev 1 and describes an older model. Where the two
disagree, the code and the tests win. Diagrams are Mermaid, which GitHub renders inline.

## 1. Runtime

```mermaid
flowchart LR
    B([Browser])
    subgraph compose["docker compose up --build"]
        W["web<br/>nginx:alpine<br/>Angular build + SPA fallback"]
        A["api<br/>aspnet:10.0 :8080<br/>GET /api/accounts<br/>GET /api/accounts/{id}/pulse"]
        S["seed (one-shot)<br/>same image<br/>seed /app/db/seed.sql"]
        D[("sqlserver<br/>SQL Server 2022<br/>healthcheck: sqlcmd")]
    end
    B -- "http://localhost:8080" --> W
    W -- "/api/* proxy_pass" --> A
    A -- "EF Core + SqlQueryRaw" --> D
    S -- "migrate, load fixture,<br/>rebuild week_buckets" --> D
    D -. "service_healthy" .-> S
    S -. "service_completed_successfully" .-> A
```

Start order is `sqlserver` (healthy) → `seed` (exits 0) → `api` → `web`. The seed step is
idempotent: the fixture loads once, and `week_buckets` is regenerated on every run. nginx serves
`/api` on the same origin, so there's no CORS. For local dev without containers, `ng serve`
(:4200, `proxy.conf.json`) talks to `dotnet run` (:5117) instead, with only `sqlserver` in Docker.

## 2. `GET /api/accounts/{id}/pulse?week=YYYY-MM-DD`

```mermaid
sequenceDiagram
    autonumber
    participant V as Angular PulseView
    participant N as nginx (web)
    participant E as PulseEndpoints (Api)
    participant Q as PulseQuery (Api)
    participant DB as SQL Server
    participant C as PulseBuilder + Statistics (Core)

    V->>N: /accounts/6/week/2026-06-01 → id(), week() signal inputs → httpResource
    N->>E: GET /api/accounts/6/pulse?week=2026-06-01
    E->>E: parse week (else 400 pulse.invalid_week)
    E->>DB: accounts by id (else 404 pulse.account_not_found)
    E->>DB: week_buckets WHERE timezone = account tz AND is_complete = 1
    E->>E: week must be a complete Monday (else 400), default = last complete week
    E->>Q: RunAsync(id, first complete week, target)
    Q->>DB: accounts ⨝ week_buckets (tz, range) ⨝ activity_events_dedup, GROUPING SETS
    DB-->>Q: WeeklyCount rows at location grain + account grain
    Q-->>E: rows
    E->>C: PulseBuilder.Build(completeWeeks, target, rows)
    C->>C: zero-fill every prior complete week
    C->>C: Baseline.Lambda: trailing 12, min 8, drop 1 high + 1 low, mean
    C->>C: Significance.Assess: exact Poisson tails, gate, BandFor, verdict, rank key
    C-->>E: account row + locations sorted by rank key, then name
    E-->>N: 200 JSON (verdicts snake_case)
    N-->>V: JSON → rows rendered in server order, never re-sorted
```

The aggregation runs as one parameterised statement. It joins events onto the account timezone's
precomputed weeks with a range join (`utc_start <= occurred_at < utc_end`), which makes weeks
account-local and DST-correct. The query fetches every complete week up to the target, not just
the 12-week window: zero-fill is only correct if "no rows" means "no events". An account with no
events (account 20) returns 200 with `state: "empty"` and `insufficient_history`.

## 3. Data model

```mermaid
erDiagram
    accounts ||--o{ activity_events : "account_id (FK, restrict)"
    activity_events }|--|| activity_events_dedup : "GROUP BY all but id, MIN(id)"
    accounts }o..o{ week_buckets : "timezone = timezone (no FK)"
    week_buckets ||..o{ activity_events_dedup : "utc_start <= occurred_at < utc_end"

    accounts {
        int id PK "explicit, no IDENTITY"
        varchar name "120"
        varchar industry "60"
        varchar timezone "IANA id, one per account"
        datetime2 created_at "UTC, NOT start of history"
    }
    activity_events {
        int id PK
        int account_id FK
        varchar location "80, label reused across accounts"
        varchar event_type "call_received, appointment_set, lead_created"
        datetime2 occurred_at "UTC"
        int duration_seconds "nullable, noise"
        varchar outcome "nullable, per-type enum"
    }
    activity_events_dedup {
        int id "MIN(id) of the group"
        int account_id
        varchar location
        varchar event_type
        datetime2 occurred_at
        int duration_seconds
        varchar outcome
    }
    week_buckets {
        varchar timezone PK
        date week_start_local PK "Monday, local"
        datetime2 utc_start "inclusive"
        datetime2 utc_end "exclusive"
        bit is_complete "all 7 local days in window"
    }
```

`accounts` and `activity_events` mirror `db/schema.sql` (EF migration `InitialSchema`).
`activity_events_dedup` is a view (migration `ActivityEventsDedupView`) that takes 12,626 raw rows
down to 12,614, and every reporting read goes through it. `week_buckets` is keyed by timezone,
not by account (6 zones × 27 weeks = 162 rows, 150 complete). `SeedImporter` builds it in C# with
`TimeZoneInfo` from the global `MIN/MAX(occurred_at)`, never from the wall clock.

## 4. Verdict

```mermaid
flowchart TD
    S["count n for the target week,<br/>prior complete weeks zero-filled"] --> H{"prior complete weeks ≥ 8?"}
    H -- no --> IH["insufficient_history<br/>λ, band, p = null · rank +∞"]
    H -- yes --> L["λ = mean of trailing 12,<br/>minus 1 highest and 1 lowest"]
    L --> G{"λ #lt; 3.7?"}
    G -- yes --> NV["not_enough_volume<br/>λ, p shown, band null · rank +∞"]
    G -- no --> PL{"p_low = P(X ≤ n) #lt; 0.025?"}
    PL -- yes --> BE["below"]
    PL -- no --> PH{"p_high = P(X ≥ n) #lt; 0.005?"}
    PH -- yes --> AB["above"]
    PH -- no --> NO["normal"]
    BE & AB & NO --> R["band = BandFor(λ)<br/>rank = min(p_low/0.025, p_high/0.005)"]
```

The constants are `Baseline.Window = 12`, `Baseline.MinWeeks = 8`, `Significance.MinLambda = 3.7`,
`AlphaLow = 0.025` and `AlphaHigh = 0.005`. The band is the set of integer counts that `Assess`
would call normal, found by binary search on the same tails, so the band and the verdict can't
disagree. The rank key is < 1 exactly when a row is flagged, so flagged rows always sort first.

## 5. Verification

```mermaid
flowchart LR
    SEED[("db/seed.sql<br/>never edited")] --> PS["tools/profile_seed.py --dedupe<br/>counts, window, dedupe, timezones"]
    PS --> OP["tools/oracle_poisson.py<br/>lgamma + fsum tails, linear band scan"]
    PS & OP --> G["goldens<br/>CLAUDE.md table"]
    G --> U["Core unit tests<br/>xUnit, no Docker"]
    G --> I["integration tests<br/>Testcontainers SQL Server +<br/>SeedImporter + WebApplicationFactory"]
    OP --> V["tools/verify_aggregates.py<br/>live API, oracle at run time"]
    I --> M["mutation check<br/>dedupe mutant: 4 tests fail<br/>zero-fill mutant survived → new test"]
```

The Python oracles are a separate implementation in another language. They use different
algorithms from the C#: per-term `lgamma` + `fsum` rather than a log-space recurrence, and a
linear band scan rather than a binary search. A golden that no oracle prints isn't a golden.
Integration tests skip with a message when Docker is absent, so `dotnet test` passes either way.
`verify_aggregates.py --base-url http://localhost:8080` runs the same cross-check through the
nginx container.
