# Online fueling load measurement

Run 2026-11-19T06:33:02.626922600Z on the embedded PostgreSQL of the test suite (Zonky, PostgreSQL 18.6 on x86_64-windows, compiled by msvc-19.44.35228, 64-bit), OpenJDK 64-Bit Server VM 25.0.2+10-LTS, 12 logical processors. Client, API and database share one developer laptop: this is not production hardware.

## Setup

- 5 operators, 10 stations, 40 devices (each activated with its own P-256 key, a signed token, an agent signed in and the PIN changed), 100 spread companies + 1 hot company + 1 warm-up company, 4200 vehicles with a tag, created through the API in 54.9 s (8400 setup calls).
- HikariCP maximum pool size: 10 (the application default). 20 request threads.
- Vehicle creation (`POST /ops/vehicles`, 8 concurrent callers, n=4200): p50 8.1 ms, p95 17.6 ms, p99 27.8 ms, max 118.2 ms
- Tag issue (`POST /ops/vehicles/{id}/tags`, 8 concurrent callers, n=4200): p50 9.3 ms, p95 19.0 ms, p99 31.0 ms, max 148.4 ms

## Online fuelings

- 20 threads × 200 fuelings = 4000 requests after a warm-up of 200; 1000 to the hot company (one wallet), 3000 to 100 spread companies.
- Wall time 11.74 s, throughput 340.6 fuelings/s.
- Latency, all: p50 48.0 ms, p95 124.5 ms, p99 181.7 ms, max 490.0 ms
- Latency, hot company: p50 83.8 ms, p95 172.8 ms, p99 234.6 ms, max 490.0 ms
- Latency, spread companies: p50 43.5 ms, p95 67.9 ms, p99 84.5 ms, max 143.7 ms
- Outcomes: 4000 completed, 0 server errors, other: [].
- Deadlocks reported by PostgreSQL during the run: 0.

## Money checks

- 102 wallets: balance = credited − charged = sum of ledger entries = last balance_after: all equal.
- 10 station payables: cached 126000000 = from entries 126000000; total charged 126000000 piastres (expected 4200 × 30000 = 126000000).
- Unbalanced fueling transactions: 0. Fueling rows for the measured companies: 4000.
