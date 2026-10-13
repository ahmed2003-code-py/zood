# Hardware Spike — POS Terminal, Tags, Printer

**Status:** Planned, not started
**Related PRD sections:** §5 Phase 0, §6.1 Station POS App, §7 Offline Mode, §11 Hardware Specifications
**Decisions this spike unblocks:**

1. Tag model, memory layout and key scheme (PRD §7.3, §11 "Tag Procurement")
2. POS client framework: Kotlin/Jetpack Compose (current preference) vs Flutter (named in PRD §2.1)
3. Receipt rendering approach (text vs bitmap) for Arabic on 58mm thermal paper
4. Kiosk / device-management approach
5. Device identity source for Device Binding
6. Offline storage and background-sync parameters

**Rule:** nothing in this document is assumed to work. Every item is a hypothesis with a test and a pass criterion. Results go into `hardware-spike-results.md` and two ADRs (tag selection, POS framework).

---

## 1. Equipment

| Item | Qty | Why |
|---|---|---|
| Sunmi P2 (or current P-series successor) | 2 | Two devices are required to test cross-device cooldown (PRD §7.3) |
| Geidea Android POS | 1 | PRD names it as a target. **First confirm whether Geidea allows third-party app installation/distribution and SDK access.** |
| NTAG 213/215/216 stickers | 10 | Baseline only (UID is cloneable) |
| NTAG 424 DNA | 10 | AES auth, secure unique messaging |
| MIFARE DESFire EV3 (2K/4K) | 10 | AES auth, files, transaction backup (anti-tearing) |
| MIFARE Ultralight AES | 10 | Cheaper AES option |
| On-metal and tamper-evident (destroy-on-removal) variants of the shortlisted tags | 5 each | Real mounting conditions |
| 58mm thermal paper rolls | 10 | Receipt length and print tests |
| SIM cards from two carriers, Wi-Fi AP, a phone hotspot, a Faraday bag | — | Connectivity tests. **Do not use RF jammers** (illegal); use a Faraday bag / airplane mode to simulate zero signal. |

---

## 2. Test areas

### 2.1 NFC technology and read performance

| Validate | How | Pass |
|---|---|---|
| Which tag families the device reads via standard Android NFC vs. vendor SDK | Read each sample tag with a minimal test app | Shortlisted tags readable through a supported API |
| Read time | Measure tap-to-ID 50× per tag | p95 ≤ 300 ms (leaves room for the PRD offline target of ≤ 1.5 s scan-to-receipt) |
| Read distance and angle through windshield glass | Mount on glass, test from inside/outside | Reliable read at a practical attendant posture |
| Reads with tinted / metallized windshields | Test on at least one tinted car | Documented; defines which mounting position to use |

### 2.2 Secure tag reading (anti-cloning)

| Validate | How | Pass |
|---|---|---|
| Mutual AES authentication works on-device | Authenticate with a diversified key per tag | Works within the read-time budget |
| A cloned UID is rejected | Copy UID of an NTAG to a magic card; try both | Cloned card fails authentication on the shortlisted tag type |
| Key handling | Where do keys live on the device? Android Keystore, vendor secure element, SAM slot? | A design where no master key is extractable from the POS app |

### 2.3 Secure tag writing (cooldown record)

Candidate record (to be validated, not decided): `version | last_fuel_at (UTC epoch s) | cooldown_until | short_tx_id | device_seq | counter | MAC` ≈ 32 bytes.

| Validate | How | Pass |
|---|---|---|
| Write + read-back time | 50 writes per tag type | p95 ≤ 400 ms |
| Write protected by authentication | Attempt write without key | Rejected |
| Cross-device enforcement | Fuel on device A (offline), scan same tag on device B (offline) | B shows cooldown rejection (PRD §7.3) |
| Write endurance | Loop writes; compare with vendor spec | Endurance ≫ expected lifetime fuelings (e.g., 3/day × 5 years) |

### 2.4 Power-loss / tearing during NFC writes

| Validate | How | Pass |
|---|---|---|
| Tag pulled away mid-write | Repeatedly remove tag during write (100 attempts) | Tag ends in either the old or the new valid record, never a corrupt one (use anti-tearing / backup-file features or a two-slot + counter layout) |
| Device power loss mid-write | Pull battery / force shutdown during write | Same as above; the app detects a failed write and flags it (PRD §7.3 step 6) |
| Transaction remains valid when the write fails | Force write failure | Transaction completes, tag-write-failure flag recorded |

### 2.5 Tag physical environment

| Validate | How | Pass |
|---|---|---|
| Heat (parked cars in Egyptian summer) | Check vendor temperature rating; heat-test samples if possible | Operating/storage range covers windshield temperatures |
| Adhesive and tamper evidence | Attempt removal and reattachment | Tag is destroyed or visibly damaged on removal |
| Trucks / metal surfaces | Mount on-metal variants on a truck | Reliable read |

### 2.6 Thermal printer

| Validate | How | Pass |
|---|---|---|
| SDK access and print API | Print a test receipt from our app | Works without vendor app in the loop |
| Status detection: paper out, cover open, overheating, jam | Trigger each condition | Each is reported to the app before or after the print call (feeds the Receipt Fail-Safe, PRD §6.1) |
| Receipt length | Print the target compact receipt, measure | ≈ 7–9 cm |
| Print time | Measure | ≤ 1.5 s for the compact receipt |
| Energy/heat | Print 100 receipts consecutively | No overheating stop; no dense black areas in layout |

### 2.7 Arabic printing

| Validate | How | Pass |
|---|---|---|
| Native text printing shapes Arabic correctly (joined letters, RTL, mixed Arabic + digits + Latin) | Print sample strings: station name, plate "أ ب ج 4582", "بنزين 92", amounts | Correct shaping and order |
| Built-in font quality at small size | Visual check | Legible at the chosen size |

### 2.8 Bitmap fallback

| Validate | How | Pass |
|---|---|---|
| Render the whole receipt as a 384-px-wide monochrome bitmap | Render with our font, print via raster API | Correct Arabic, ≤ 2 s total, length unchanged |
| QR code size and scan reliability | Print 15 mm / 18 mm QR, scan with 3 phones | Scans reliably at the smaller size |

### 2.9 Camera

| Validate | How | Pass |
|---|---|---|
| Plate capture in daylight, night, and under station canopy | Capture 30 plates per condition | Quality sufficient for on-device first-pass OCR (PRD §2.1) |
| Arabic plate OCR feasibility on-device | Evaluate at least one on-device OCR option | Measured accuracy and latency documented; never blocks completion (PRD §6.1) |

### 2.10 Device identity

| Validate | How | Pass |
|---|---|---|
| Which identifiers are available to our app (serial via vendor SDK, Android ID, hardware attestation) | Read each on both device models | A stable identifier that survives app reinstall, plus a key pair in Android Keystore for binding |
| Hardware-backed keystore / StrongBox availability | Query key attestation | Documented |

### 2.11 Kiosk mode and device-owner policies

| Validate | How | Pass |
|---|---|---|
| Device-owner provisioning (QR / vendor MDM) | Provision a factory-reset device | Our app is device owner or managed by an approved MDM |
| Lock task (kiosk) mode | Enable lock task | Attendant cannot leave the app; emergency exit only via admin PIN/MDM |
| Policies: block app installs, USB debugging, factory reset, safe boot, status-bar pull-down, settings access | Apply each restriction | All enforceable |
| App updates in kiosk | Push an update through the chosen channel | Updates install without attendant action |

### 2.12 Wi-Fi restrictions

| Validate | How | Pass |
|---|---|---|
| Restrict Wi-Fi configuration (allow only station networks) | Apply device-owner restriction | Attendant cannot join other networks |

### 2.13 Hotspot / tethering restrictions

| Validate | How | Pass |
|---|---|---|
| Disable tethering / hotspot | Apply restriction | Device cannot share its data connection |

### 2.14 4G

| Validate | How | Pass |
|---|---|---|
| Data on both carriers at the pilot site | Measure latency and drop rate | Documented per carrier |
| Network switching Wi-Fi ↔ 4G mid-sync | Toggle during sync | Sync resumes without duplicates |

### 2.15 GPS

| Validate | How | Pass |
|---|---|---|
| Time to first fix and accuracy under canopy | Measure at the pilot station | Documented; informs geofence tolerance (PRD Phase 3) |
| Behaviour with location disabled | Disable location | App behaviour defined (flag, not block) |

### 2.16 Offline encrypted storage

| Validate | How | Pass |
|---|---|---|
| Encrypted DB performance (e.g., SQLCipher or framework equivalent) | Insert/read 30 days of transactions (PRD §7.2) | Insert p95 ≤ 50 ms; storage footprint fits device |
| Key protected by hardware keystore | Inspect | Key not extractable |
| Data survives reboot, app update, low storage | Test each | No loss of pending transactions |

### 2.17 Background sync

| Validate | How | Pass |
|---|---|---|
| Sync runs under Doze / vendor battery optimizations | Leave device idle with pending transactions | Sync completes within the target window after reconnection |
| Retry after app kill and reboot | Kill/reboot with pending queue | Queue preserved; sync resumes; no duplicates server-side |

### 2.18 Clock and time

| Validate | How | Pass |
|---|---|---|
| Clock drift while offline for 24–72 h | Compare against reference | Drift documented; feeds sync sanity window |
| Attendant changing system time | Attempt change under device-owner policy | Blocked, or detected and flagged |

### 2.19 End-to-end latency (PRD §9)

| Validate | How | Pass |
|---|---|---|
| Online scan-to-response | Full flow against a staging API | ≤ 2 s |
| Offline scan-to-receipt | Full flow offline | ≤ 1.5 s |

### 2.20 Framework evaluation

| Validate | How | Pass |
|---|---|---|
| Vendor SDK availability for NFC secure ops, printer, kiosk | Check SDK docs and samples for Kotlin and Flutter (platform channels) | Choose the framework with the least custom bridging for secure NFC, printer, and kiosk |

---

## 3. Outputs

- `docs/architecture/hardware-spike-results.md`: measured results per test above
- ADR: tag model, memory layout, key diversification scheme
- ADR: POS client framework
- Updated receipt specification (text vs bitmap, final length)
