# ADR 0003: Hand-written SVG charts instead of a chart library

- **Status:** Accepted
- **Date:** 2026-10-31

## Context

[PLAN.md §2.1](../PLAN.md) said a Blazor chart library that works offline in Blazor Hybrid would be chosen when the first chart was built: the weight trend in Phase 1, step 1.1. That chart is raw daily weights plus one smoothed line, over at most a couple of years of points.

A library would add a package (often with JavaScript assets bundled into the app) to the MAUI app's dependency and update surface. It would need checking for offline use on Android and Windows, and its accessibility output would need checking for the BlazorWebView's screen readers.

## Decision

Draw charts as SVG from shared Razor components in `Health360.UI`, styled by the existing theme (`wwwroot/app.css`). No chart package.

- **Calculations stay in `Health360.Domain`.** For example, `Body/WeightTrend` produces the trend points; the component only maps points to coordinates and draws them.
- **The x axis uses real dates**, so gaps between weigh-ins show as gaps.
- **Values are converted** to the user's display unit through `Units` before drawing.
- **Each chart has an accessible description and a data table** (visually hidden, or behind a toggle) with the same values, so screen readers on Android and Windows get the numbers.
- **Edge states are explicit:** no data, a single point, and a constant value (a flat line, not a division by zero).

## Consequences

- No new dependency, nothing to load from a CDN, and it works offline by construction.
- We own axis ticks, scaling and layout for both the phone and desktop widths. That is small for line charts.
- Interaction (hover, zoom, panning) is limited to what we build.

**Revisit this decision** when any of these happen: charts need rich interaction (zoom, brushing, linked tooltips); a chart needs to draw many thousands of points; or enough chart types accumulate that maintaining our own chart code costs more than a library would. A replacement library must work offline in Blazor Hybrid, with no CDN assets, and be recorded in a new ADR.
