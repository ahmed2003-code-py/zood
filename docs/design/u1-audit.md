# U1 audit: what makes the portals feel unfinished

Walked on 2026-11-20 at 390, 768 and 1440 px, in Arabic and English, Light and Dark.

- **Demo screens** (mock mode, the default) were captured as they are.
- **Live screens** (`src/live`, what the owner tested on zawed-dev) were captured in `VITE_API_MODE=http`
  against a fixed API stub (`apps/web/e2e/live/api-stub.ts`), because no API server runs on this machine.

Screenshots: `/home/ahmedeldeeb/Videos/zawed/wt/u1-shots/before/` (`mock/`, `live/`, `dialogs/`).

There are no crashes and no broken flows. The discomfort comes from many small things that do not
agree with each other. Ranked by how much each one costs the person using the screen:

| # | Problem | Where | Why it hurts |
|---|---|---|---|
| 1 | **Forms and details slide in as a full-height side panel.** At 1440 px a 448 px strip covers a third of the screen. Short forms leave 300 px of white under the last field, the buttons sit at the far bottom corner, and the light overlay barely separates the panel from the page. | `Sheet`, about 70 uses through `FormSheet` and `ReasonSheet` | The owner's main complaint. It feels like a settings drawer, not a focused task. |
| 2 | **No surface layering.** Cards, tables and the page differ only by a hairline. In Dark the page, cards and sidebar are three almost identical near-blacks, so the brand disappears. | tokens, `Panel`, `ListPanel`, Dark sidebar | Everything has the same weight, so nothing leads the eye. |
| 3 | **Tables are squeezed into narrow columns.** On the Ops company page the vehicles table sits in a two-thirds column and wraps "Toyota / Hilux" and "بنزين / 92" onto two lines. Headers scroll away on long lists. | `DataView`, Ops company page | Rows double in height and the eye loses the line. |
| 4 | **Buttons do not form a system.** The same row mixes outlined, ghost and filled buttons of different heights. A disabled primary in Dark looks like a different, muted green rather than disabled. Ghost buttons look like plain text. | `Button`, station page, top-ups, agents | People hesitate before every action. |
| 5 | **Page headers are loose.** Actions float far away from the title. Back links and breadcrumbs repeat each other. The detail title, for example "Fueling FL-2611-4821", sits under a breadcrumb that says "Transactions". | `PageHeader`, `BackLink`, `DesktopHeader` | The top of every page reads as scattered pieces. |
| 6 | **Top-up and approval lists are stacked text.** The amount, reference, date, account, status and actions all run in one column at the start edge. The decision buttons sit in a different place on each row. | `TopUpRow`, Ops top-ups, company wallet | Approving money should be the calmest screen, and it is the busiest. |
| 7 | **Filters take a phone screen.** On 390 px a search box, a select and a segmented control stack to 250 px before the first row. Native date inputs show "mm/dd/yyyy" in an Arabic page. | users, vehicles, fuelings | The list starts below the fold. |
| 8 | **The KPI accent is a thick coloured side border** (`border-s-4`). It reads as an alert stripe, not as "the main figure". | `KpiTile`, company dashboard, station finance | It looks like a warning and fights the rest of the card. |
| 9 | **The type scale is flat.** Page titles are 24 px, section titles 16 px and labels 14 px, with weight doing most of the work. Table headers are the same grey as captions. | everywhere | Hierarchy is hard to read at a glance. |
| 10 | **Inputs are boxy.** A heavy 1 px sand-400 border, no hover state, and the native select arrow. The focus state only changes the border colour. | `TextField`, `SelectField` | Forms look like defaults rather than designed. |
| 11 | **The empty, loading and error states are all the same grey circle.** The error state is hard to tell from "nothing here yet". Loading is four grey bars whatever the content. | `EmptyState`, `ErrorState`, `PagedList` | The states do not explain themselves. |
| 12 | **The file picker is the browser's own.** "Choose File / No file chosen" stays in English on an Arabic page. | Ops import | A raw browser control in the middle of a designed screen. |
| 13 | **RTL details.** A legal name ending in "S.A.E." shows its full stop at the wrong end. Long emails in lists are cut at the wrong side. A transfer date shows as raw `2026-11-18`. | company form, users list, top-ups | Small things that tell an Arabic reader that the screen was built in English. |
| 14 | **The dialog submit button on edit forms says "Edit"** (تعديل) instead of "Save". | company, station, vehicle and operator forms | It names the wrong action. |
| 15 | **Status chips are flat colour pills** with no edge, so in Light the warning chip almost merges with sand cards. | `StatusChip` | Status should be the first thing seen in a row. |
| 16 | **Toasts appear over the bottom navigation** with no tone colour apart from the icon. | `Toaster` | Easy to miss. |
| 17 | **Navigation.** The desktop header has no background separation when the page scrolls under it. The tablet rail has no tooltip-like labels. The Dark sidebar has no brand colour. | shell | The frame feels unfinished. |

## Status on this branch

| # | Status |
|---|---|
| 1 | Fixed. `Sheet` is a centred dialog from 640 px (sizes `sm`–`xl`) and a bottom sheet on phones. Focus returns to the button that opened it. |
| 2 | Fixed. Cards carry a hairline and a light shadow. The Dark theme has three distinct surface steps and a green sidebar. |
| 3 | Fixed. The Ops company page uses tabs, so the vehicles table gets the full width. Table headers stay in view. |
| 4 | Fixed. Disabled filled buttons turn neutral; one size system (40 px with a mouse, 44 px on touch). |
| 5 | Mostly fixed. `PageHeader` aligns its actions with the title. Detail pages still show a back link and a breadcrumb, and pages outside the menu (a vehicle) show only the portal name in the breadcrumb. |
| 6 | Fixed. Top-up rows keep the status and the decision buttons together at the inline end. |
| 7 | Partly fixed. Filters became the list card's toolbar, and the date range is paired. On a phone the filters still take about 200 px. Native date inputs are unchanged. |
| 8, 9, 10, 11 | Fixed in the shared components. |
| 12 | Fixed (`FileField`). |
| 13 | Fixed for the legal name, transfer dates and device/agent codes. Long emails in lists are still cut at the end of the line. |
| 14 | Fixed ("Save changes"). |
| 15, 16, 17 | Fixed. The tablet rail still names items through the browser tooltip only. |
| New | Fixed: a five-option segmented control pushed the Arabic top-ups page sideways at 390 px. It now scrolls inside itself, and the live baselines check every page for sideways scrolling. |

## Out of scope

- The public landing page and the sign-in screen (other branches).
- The POS simulator: it already uses large controls and stays Light by default.
- Native date inputs follow the browser's language. They are left as they are; a custom date picker is a
  separate decision.
