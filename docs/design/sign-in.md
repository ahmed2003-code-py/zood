# Sign-in page (pass 3)

Pass 2 split the page into a form and a brand panel. The owner did not like it (2026-11-21): it was
not centred, it repeated the landing page, its motion was not what he meant, and nothing showed
that you had really signed in. Pass 3 replaces it with one centred card on a backdrop built from
the logo's own marks, and a fuel drop that tells the sign-in as it happens.

Code: `apps/web/src/auth/AuthLayout.tsx` (the frame) and `src/auth/stage/`. Copy:
`src/i18n/locales/{ar,en}/site.json` (`signIn.*`).

## Where it is used

`AuthLayout` frames `/login` (credentials, second factor, set-up), `/account/password` and the
"wrong portal" screen (`NoAccess`, loaded lazily from `RequirePortal`). They all share the card, the
drop and the backdrop.

## Layout

- A minimal header: the logo (back to `/`), then language and theme.
- One card, centred, at most 27 rem wide, with the emblem sitting on its top edge. The title and
  the description are centred; the fields are full width.
- One small line under the card says this is the one sign-in for fleet companies, partner stations
  and the ZAWED team.
- Small footer links: home, privacy, terms, contact.
- On phones the same card fills the width with a 16 px gutter. At 360 px there is no sideways
  scrolling, and the controls are 44 px on touch screens.

## The backdrop (`FuelBackdrop.tsx`, `backdrop.css`)

The backdrop uses the logo's marks, copied in `stage/marks.ts` and checked against the generated
geometry by `marks.test.ts`:

- groups of the three speed lines stream across in the reading direction, like fuel in a line;
- a few small drops rise and fade;
- a large soft drop breathes behind the card;
- two fuel waves roll along the bottom edge.

In Light the backdrop is warm sand with green and orange marks. In Dark it is a deep neutral where
the green glows. There are no flat green areas.

It uses transforms and opacity only. The layer is fixed to the viewport and has `contain: strict`.
It mounts one frame after the card paints and pauses while the tab is hidden. Under reduced motion
it is a still composition. On phones every other stream is hidden.

It ships in the sign-in route's chunk. A nested `React.lazy` there made the bundler fold the whole
design-system chunk into the entry (+380 kB), so it is a static import that mounts late.

## The fuel drop and the sign-in moment

The emblem (`FuelDrop.tsx`) is the logo's drop with its three speed lines. The drop holds fuel. Its
level is a registered CSS property (`--fuel`), so it transitions. The whole page carries
`data-state`, and the drop, the card, the button and the backdrop all read it:

| State | What happens |
|---|---|
| `idle` | A little fuel and a slow wave. |
| `working` (request running) | The drop fills to about 86 % over 2.8 s and the speed lines stream into it. The button keeps its colour, says "Signing in…" / "جارٍ تسجيل الدخول…" with a fuel icon, and fills from the reading edge like a tank. |
| `success` | The drop fills to the top, a ring goes out once and a check draws itself. The card shows "Welcome, Mona" / "أهلًا بك يا Mona" (the first word of the user's full name; "Welcome to ZAWED" without one). The detail line is "Opening your area…", or "Next, choose your own password." when a password change is due. The card lifts and then dissolves while the big drop surges. |
| `error` | The error text shows at once. The card shakes gently (420 ms; a second refusal shakes again) and the drop drains, then settles back. |

**Timing.** The welcome lasts `SUCCESS_MS` (950 ms; a unit test keeps it at or under a second).
After that the app navigates. The destination's portal chunk starts loading as soon as the sign-in
succeeds (`stage/preload.ts`), so the moment overlaps the portal's own loading time and adds as
little as possible. If the portal is slower, the backdrop stays visible, never a blank page.

**Under reduced motion** nothing moves: levels change instantly, and a successful sign-in opens the
portal straight away, with no welcome.

The second factor, the authenticator set-up and the password change use the same card, drop and
button ("Checking the code…", "Saving…").

## Behaviour kept

All of these work as before:

- the error texts and the lockout message;
- an expired second-factor challenge sends you back to the password;
- `?next=` is kept, including across a forced password change;
- one sign-in for every role;
- if you are already signed in, the page sends you straight on;
- the `/admin/login` bookmark still works.

## Performance

The entry chunk is 310.49 kB (98.99 kB gzip), slightly smaller than before (310.63 kB). The sign-in
frame chunk (`AuthLayout`, now holding the backdrop) is 7.9 kB JS plus 7.5 kB CSS; the old brand
panel chunk is gone. There are no new dependencies.

## Tests

`e2e/sign-in.spec.ts` covers:

- the centred card, with the form first in keyboard order;
- a successful sign-in: the button busy, the drop working, then success, the welcome with the
  first name, and `next` kept;
- a refusal: the alert, the shake (again on a second refusal) and the drop draining;
- the second factor in Arabic, ending in the welcome;
- a phone at 360 px;
- reduced motion: no running animations, and the portal opens with no welcome;
- the old `/admin/login` bookmark.

The API is stubbed with `page.route`.
