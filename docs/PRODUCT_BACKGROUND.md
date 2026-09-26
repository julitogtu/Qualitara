# Relay — Product Background

**Relay** is a B2B SaaS that helps service businesses track inbound customer activity — calls, leads, appointments — across their locations. Customers range from single-site shops to groups with 15+ locations.

Each customer gets a **reporting dashboard**. Today it shows raw totals per location and not much else.

Two things the team keeps hearing:

- **Account managers** say customers ask *"is this number normal for us?"* and can't answer it from the dashboard.
- **The support team** says customers with multiple locations struggle to spot which location needs attention.

## How the team works

The engineering team works **spec-driven and AI-first**: engineers pick up tickets like the one in `TICKET.md`, build an AI-assisted implementation plan, and implement with AI coding agents by default. Judgment concentrates on planning, spec interpretation, and reviewing/hardening agent output.

## Data model

Two tables (see `schema.sql`):

- **`accounts`** — one row per customer. Includes the account's IANA `timezone`.
- **`activity_events`** — inbound activity: `call_received`, `lead_created`, `appointment_set`. Each event has a `location` (the account's site/branch), an `occurred_at` timestamp **in UTC**, an optional `duration_seconds` (calls), and an optional `outcome`.
