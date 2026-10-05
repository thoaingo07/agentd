# Phase 3 — Tasks

The detailed implementation tasks for [Phase 3: BFF + Web UI v1 + browser security](../README.md), in build order.

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T3.1 | [Event store and live publisher](01-event-store-and-publisher.md) | Phase 1 (`events` table, `RecordAgentOutput`) | L | ☑ |
| T3.2 | [BFF endpoints and view models](02-bff-endpoints-and-view-models.md) | T3.1, Phase 2 (`SubmitDeveloperMessage`) | M | ☑ |
| T3.3 | [OpenAPI document and generated TypeScript types](03-openapi-and-ts-types.md) | T3.2 | S | ☑ |
| T3.4 | [SignalR events hub with replay](04-signalr-events-hub.md) | T3.1 | M | ☑ |
| T3.5 | [Antiforgery with HttpOnly cookies](05-antiforgery-and-cookies.md) | T3.2 | M | ☑ |
| T3.6 | [Security headers, strict CSP and CSP reporting](06-security-headers-and-csp.md) | T3.2, Phase 0 (web build in `wwwroot`) | M | ☑ |
| T3.7 | [Web API layer and Pinia stores](07-web-api-layer-and-stores.md) | T3.3, T3.4, T3.5 | L | ☑ |
| T3.8 | [Reusable `Ag*` components (base-ui-vue + daisyUI)](08-ag-components.md) | Phase 0 (theme, web scaffold) | M | ☑ |
| T3.9 | [App shell and Dashboard view](09-dashboard-view.md) | T3.7, T3.8 | M | ☑ |
| T3.10 | [Session view: transcript, diff, details, composer](10-session-view.md) | T3.7, T3.8, T3.9 | L | ☑ |
| T3.11 | [History view with server-side paging](11-history-view.md) | T3.2, T3.9, T3.10 | S | ☑ |
| T3.12 | [Playwright smoke tests and BFF security test suite](12-e2e-and-bff-security-tests.md) | T3.4, T3.5, T3.6, T3.9, T3.10, T3.11 | M | ☐ |
| T3.13 | [Work item view: one timeline across jobs](13-work-item-view.md) | T3.2, T3.4, T3.7, T3.10 | M | ☐ |
| T3.14 | [Cloudflare Access sign-in mode](14-cloudflare-access-auth.md) | T3.5 | S | ☑ |
