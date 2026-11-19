# Query plan report

Generated 2026-11-19T06:14:47.659645700Z by QueryPlanReport.

## Environment

- PostgreSQL: PostgreSQL 18.6 on x86_64-windows, compiled by msvc-19.44.35228, 64-bit
- JVM: OpenJDK 64-Bit Server VM 25.0.2+10-LTS
- OS: Windows 11 10.0, 12 logical processors
- Data directory: D:\zawed-perf\pgdata
- Embedded PostgreSQL (Zonky) on a developer laptop, not production hardware.
- Settings: shared_buffers=512MB; work_mem=4MB; maintenance_work_mem=256MB; effective_cache_size=4GB; random_page_cost=4; max_parallel_workers_per_gather=2; jit=on; synchronous_commit=off; fsync=off; max_wal_size=4GB; plan_cache_mode=auto;

## Dataset

| Table | Rows | Heap + indexes |
|---|---:|---:|
| companies | 100 | 88 kB |
| vehicles | 200,000 | 66 MB |
| rfid_tags | 200,000 | 70 MB |
| station_operators | 20 | 40 kB |
| stations | 300 | 168 kB |
| pos_devices | 1,000 | 392 kB |
| agents | 2,000 | 712 kB |
| fuelings | 5,000,000 | 4259 MB |
| alerts | 50,007 | 17 MB |

- fuelings heap only: 2064 MB; time span 2025-11-18 03:09:29.01295+02 .. 2026-11-19 02:14:58+02
- status/origin mix: COMPLETED/OFFLINE=475246, COMPLETED/ONLINE=4274486, DECLINED/ONLINE=250268
- largest company: 19869 vehicles, 496,504 fuelings; median company: 1404 vehicles, 35,365 fuelings
- operator used: 249,886 fuelings; station used: 16,926 fuelings
- windows: old month 2026-05, last full month 2026-10, dataset end 2026-11-19T00:14:58Z (all months in Africa/Cairo)
- load time: 748 s (the fuelings same-tenant trigger disabled during the bulk insert, then re-enabled; foreign keys and CHECK constraints enforced; same-tenant rule verified set-based afterwards)
- existing fuelings indexes: fuelings_client_txn_id_key 195 MB; fuelings_company_id_idx 435 MB; fuelings_ledger_transaction_key 8192 bytes; fuelings_offline_conflicts_idx 8080 kB; fuelings_offline_exposure_idx 35 MB; fuelings_operator_id_idx 427 MB; fuelings_payable_held_idx 8192 bytes; fuelings_pkey 151 MB; fuelings_reference_key 261 MB; fuelings_station_id_idx 433 MB; fuelings_vehicle_usage_idx 250 MB


## Phase `baseline`

Indexes: the V1–V11 indexes only.

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 39.98 | 5.47 | 8.01 | 0.25 | 0.40 | shared hit=82 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 4.93 | 4.82 | 6.28 | 0.53 | 1.06 | shared hit=82 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 9389.04 | 2542.54 | 2641.50 | 3274.54 | 0.57 | shared hit=2293 read=121924 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 2788.73 | 3160.82 | 3551.43 | 2901.50 | 0.46 | shared hit=2298 read=122380 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 4.92 | 2.38 | 3.31 | 0.46 | 0.42 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 2.12 | 2.49 | 2.78 | 0.50 | 0.40 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 311.63 | 115.36 | 139.29 | 111.28 | 0.60 | shared hit=13183 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 2709.52 | 2862.78 | 2988.23 | 2647.66 | 0.61 | shared hit=15246 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 281.07 | 22.88 | 43.17 | 19.36 | 0.50 | shared hit=15860 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 4.13 | 1.84 | 1.97 | 0.29 | 0.47 | shared hit=54 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 1781.08 | 118.58 | 144.50 | 131.19 | 0.97 | shared hit=19327 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_station_id_idx → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 15,541) |
| operator_list_newest | OPERATOR | 51 | 7.93 | 2.29 | 2.68 | 0.61 | 0.39 | shared hit=131 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 11418.95 | 3103.05 | 3377.88 | 2833.52 | 0.74 | shared hit=5537 read=119257 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 4817.28 | 5713.90 | 6343.76 | 5624.32 | 0.55 | shared hit=60725 read=203556 | GroupAggregate → Gather Merge → Sort Method: quicksort → Parallel Seq Scan on fuelings (filter removed 1,652,697) |
| activity_company_month_median | COMPANY | 31 | 1255.18 | 122.13 | 144.90 | 108.90 | 0.50 | shared hit=33524 | GroupAggregate → Gather Merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_id_idx (filter removed 10,818) |
| activity_operator_month | OPERATOR | 31 | 7780.85 | 4197.59 | 10082.48 | 6528.80 | 0.83 | shared hit=49 read=160945 | GroupAggregate → Gather Merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 76,250) |
| activity_platform_month | PLATFORM | 31 | 6129.75 | 6166.28 | 6222.06 | 5240.55 | 0.82 | shared hit=64934 read=199347, temp read=3452 written=3461 | GroupAggregate → Gather Merge → Sort Method: external merge → Parallel Seq Scan on fuelings (filter removed 1,525,097) |
| activity_alerts_company_month | COMPANY | 31 | 74.86 | 6.46 | 6.91 | 5.65 | 0.61 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 23.29 | 20.21 | 24.58 | 19.52 | 0.34 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 5953.53 | 1626.37 | 1929.46 | 3662.42 | 0.40 | shared hit=63631 read=200574 | Gather → Parallel Seq Scan on fuelings (filter removed 1,652,686) |
| export_company_month_rows | COMPANY | 41942 | 8711.03 | 8977.40 | 14259.68 | 14019.19 | 0.53 | shared hit=65337 read=198960, temp read=452 written=453 | Gather Merge → Sort Method: external merge → Sort Method: quicksort → Parallel Seq Scan on fuelings (filter removed 1,652,686) |
| export_operator_month_count | OPERATOR | 1 | 4537.02 | 1487.85 | 1512.46 | 1287.17 | 0.90 | shared hit=40 read=160938 | Gather → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 76,205) |
| export_operator_month_rows | OPERATOR | 21271 | 1841.50 | 1442.46 | 1656.89 | 1726.08 | 0.73 | shared hit=64 read=160946 | Gather Merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 76,205) |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 2440.11 | 1997.21 | 2259.81 | 1751.17 | 1.22 | shared hit=64967 read=199312, temp read=1440 written=1443 | GroupAggregate → Gather Merge → Sort Method: external merge → Parallel Seq Scan on fuelings (filter removed 1,588,327) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 3.00 | 0.88 | 1.05 | 0.06 | 0.37 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 1.56 | 1.02 | 1.41 | 0.06 | 0.62 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 1.50 | 0.69 | 0.96 | 0.11 | 0.74 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |
| proposal_ops_list_month_old_by_time | PLATFORM | 51 | 1315.84 | 1430.32 | 1593.94 | 1506.38 | 0.42 | shared hit=64963 read=199334 | Gather Merge → Sort Method: top-N heapsort → Parallel Seq Scan on fuelings (filter removed 1,525,097) |
| proposal_ops_list_company_month_old_by_time | PLATFORM | 51 | 1072.24 | 1095.25 | 1190.79 | 1109.11 | 0.44 | shared hit=64983 read=199314 | Gather Merge → Sort Method: top-N heapsort → Parallel Seq Scan on fuelings (filter removed 1,652,697) |
| proposal_company_list_month_old_big_by_time | COMPANY | 51 | 1153.38 | 1242.66 | 1288.33 | 1277.64 | 0.47 | shared hit=64950 read=199347 | Gather Merge → Sort Method: top-N heapsort → Parallel Seq Scan on fuelings (filter removed 1,652,697) |
| proposal_operator_list_month_old_by_time | OPERATOR | 51 | 4112.18 | 1201.29 | 1224.65 | 1172.69 | 0.41 | shared hit=72 read=160938 | Gather Merge → Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 76,250) |

Generic plans (what PostgreSQL would run if it switched a prepared statement to its generic plan; parameters unknown at plan time):

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 4281.62 | 4018.95 | 4084.82 | 4379.38 | 0.03 | shared hit=64898 read=199307 | Sort Method: top-N heapsort → Gather → Parallel Seq Scan on fuelings |
| company_list_month_old_big | COMPANY | 51 | 1146.24 | 1127.60 | 1138.22 | 1132.69 | 0.03 | shared hit=64899 read=199306 | Sort Method: top-N heapsort → Gather → Parallel Seq Scan on fuelings (filter removed 1,652,697) |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 1.111 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 5942 ms.

## Phase `v16`

V16 applied by Flyway on the loaded table (the committed script: plain CREATE INDEX in one transaction) in 30.5 s.


| Added index | Size | Valid |
|---|---:|---|
| `fuelings_company_time_idx` | 194 MB | true |
| `fuelings_operator_time_idx` | 194 MB | true |
| `fuelings_evaluated_at_brin` | 80 kB | true |

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 60.16 | 1.84 | 2.29 | 0.24 | 0.39 | shared hit=82 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 2.04 | 1.63 | 1.98 | 0.22 | 0.46 | shared hit=82 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 3165.65 | 2548.50 | 2630.06 | 2804.10 | 0.57 | shared hit=2352 read=121865 written=2074 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 2669.19 | 2546.74 | 2658.08 | 2576.56 | 0.54 | shared hit=2298 read=122380 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 4.23 | 1.91 | 1.95 | 0.45 | 0.42 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 1.98 | 2.03 | 2.32 | 0.51 | 0.47 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 291.98 | 110.26 | 112.15 | 91.50 | 0.74 | shared hit=13183 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 2335.52 | 2533.29 | 2705.78 | 2374.36 | 0.46 | shared hit=15246 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 225.98 | 19.60 | 20.13 | 39.68 | 0.61 | shared hit=15860 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 7.01 | 1.65 | 1.87 | 0.29 | 0.43 | shared hit=54 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 67.55 | 19.46 | 30.37 | 17.44 | 0.56 | shared hit=1639 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_time_idx → Bitmap Index Scan on fuelings_station_id_idx |
| operator_list_newest | OPERATOR | 51 | 3.96 | 1.81 | 2.28 | 0.65 | 0.47 | shared hit=131 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 2290.47 | 2535.60 | 2686.91 | 2402.52 | 0.45 | shared hit=11097 read=113697 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 287.34 | 243.03 | 249.13 | 241.63 | 0.51 | shared hit=18404 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_time_idx |
| activity_company_month_median | COMPANY | 31 | 30.70 | 19.90 | 20.00 | 19.11 | 0.50 | shared hit=2753 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_time_idx |
| activity_operator_month | OPERATOR | 31 | 195.67 | 138.31 | 142.64 | 128.66 | 0.49 | shared hit=13565 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_time_idx |
| activity_platform_month | PLATFORM | 31 | 863.30 | 753.34 | 793.92 | 852.33 | 0.60 | shared hit=21315, temp read=3452 written=3461 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_evaluated_at_brin |
| activity_alerts_company_month | COMPANY | 31 | 54.34 | 7.92 | 12.39 | 5.79 | 0.37 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 17.21 | 16.76 | 16.90 | 16.80 | 0.29 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 206.58 | 168.57 | 173.47 | 161.27 | 0.44 | shared hit=18466 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_time_idx |
| export_company_month_rows | COMPANY | 41942 | 449.76 | 451.01 | 492.26 | 203.31 | 0.45 | shared hit=18538, temp read=659 written=660 | Gather Merge → Sort Method: external merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_time_idx |
| export_operator_month_count | OPERATOR | 1 | 181.33 | 97.59 | 103.23 | 97.71 | 0.40 | shared hit=13612 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_time_idx |
| export_operator_month_rows | OPERATOR | 21271 | 267.71 | 262.88 | 276.51 | 140.16 | 0.81 | shared hit=13612, temp read=521 written=522 | Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_time_idx |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 912.87 | 942.28 | 960.49 | 616.58 | 0.80 | shared hit=12225, temp read=1440 written=1444 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_evaluated_at_brin (filter removed 4,053) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 1.66 | 0.78 | 0.94 | 0.09 | 0.53 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 1.35 | 1.08 | 1.35 | 0.06 | 0.60 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 1.40 | 0.86 | 0.99 | 0.08 | 0.45 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |
| proposal_ops_list_month_old_by_time | PLATFORM | 51 | 391.67 | 373.48 | 386.08 | 397.74 | 0.71 | shared hit=21331 | Gather Merge → Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_evaluated_at_brin |
| proposal_ops_list_company_month_old_by_time | PLATFORM | 51 | 12.00 | 1.62 | 1.78 | 0.22 | 0.52 | shared hit=33 | Sort Method: quicksort → Index Scan Backward using fuelings_company_time_idx |
| proposal_company_list_month_old_big_by_time | COMPANY | 51 | 1.72 | 1.81 | 1.89 | 0.34 | 0.55 | shared hit=33 | Sort Method: quicksort → Index Scan Backward using fuelings_company_time_idx |
| proposal_operator_list_month_old_by_time | OPERATOR | 51 | 1.67 | 1.76 | 1.81 | 0.39 | 0.51 | shared hit=37 | Sort Method: quicksort → Index Scan Backward using fuelings_operator_time_idx |

Generic plans (what PostgreSQL would run if it switched a prepared statement to its generic plan; parameters unknown at plan time):

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 5306.70 | 4106.04 | 4153.46 | 4413.71 | 0.02 | shared hit=62706 read=204692 | Sort Method: top-N heapsort → Gather → Parallel Seq Scan on fuelings |
| company_list_month_old_big | COMPANY | 51 | 1308.97 | 1209.03 | 1359.03 | 1281.97 | 0.03 | shared hit=64530 read=202868 | Sort Method: top-N heapsort → Gather → Parallel Seq Scan on fuelings (filter removed 1,652,697) |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 1.143 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 6043 ms.

## Phase `v16_concurrent`

V16 applied by Flyway on the loaded table (CREATE INDEX CONCURRENTLY, executeInTransaction=false, postgresql.transactional.lock=false) in 56.9 s.


| Added index | Size | Valid |
|---|---:|---|
| `fuelings_company_time_idx` | 194 MB | true |
| `fuelings_operator_time_idx` | 194 MB | true |
| `fuelings_evaluated_at_brin` | 80 kB | true |

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 60.98 | 1.61 | 1.74 | 0.36 | 0.55 | shared hit=82 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 1.80 | 1.73 | 1.84 | 0.23 | 0.47 | shared hit=82 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 3304.21 | 2555.10 | 2671.64 | 2700.67 | 0.59 | shared hit=2384 read=121833 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 2529.42 | 2577.23 | 2608.33 | 2552.17 | 0.69 | shared hit=2298 read=122380 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 3.46 | 1.69 | 1.90 | 0.49 | 0.51 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 1.91 | 1.89 | 2.44 | 0.69 | 0.47 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 285.18 | 98.54 | 112.74 | 111.98 | 0.46 | shared hit=13183 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 2465.00 | 2545.97 | 2608.28 | 2335.94 | 0.46 | shared hit=15246 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 281.22 | 20.39 | 22.32 | 18.93 | 0.50 | shared hit=15860 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 6.47 | 1.66 | 1.86 | 0.30 | 0.46 | shared hit=54 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 98.63 | 19.47 | 20.92 | 16.98 | 0.64 | shared hit=1639 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_time_idx → Bitmap Index Scan on fuelings_station_id_idx |
| operator_list_newest | OPERATOR | 51 | 4.83 | 1.94 | 1.97 | 0.60 | 0.42 | shared hit=131 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 2319.84 | 2519.00 | 2537.55 | 2406.95 | 0.67 | shared hit=11098 read=113696 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 286.57 | 239.72 | 240.16 | 243.89 | 0.55 | shared hit=18404 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_time_idx |
| activity_company_month_median | COMPANY | 31 | 30.24 | 19.45 | 19.74 | 19.47 | 0.51 | shared hit=2753 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_time_idx |
| activity_operator_month | OPERATOR | 31 | 195.00 | 138.27 | 141.58 | 138.62 | 0.47 | shared hit=13565 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_time_idx |
| activity_platform_month | PLATFORM | 31 | 823.98 | 784.49 | 811.75 | 827.61 | 0.48 | shared hit=21315, temp read=3452 written=3461 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_evaluated_at_brin |
| activity_alerts_company_month | COMPANY | 31 | 53.41 | 5.56 | 5.78 | 5.00 | 0.37 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 18.80 | 16.97 | 24.86 | 29.71 | 0.25 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 207.62 | 170.61 | 178.18 | 174.08 | 0.43 | shared hit=18466 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_time_idx |
| export_company_month_rows | COMPANY | 41942 | 425.53 | 436.56 | 449.44 | 238.80 | 0.96 | shared hit=18538, temp read=649 written=650 | Gather Merge → Sort Method: external merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_time_idx |
| export_operator_month_count | OPERATOR | 1 | 171.32 | 107.14 | 110.49 | 106.45 | 0.66 | shared hit=13612 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_time_idx |
| export_operator_month_rows | OPERATOR | 21271 | 274.38 | 280.97 | 289.43 | 157.94 | 0.47 | shared hit=13612, temp read=521 written=522 | Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_time_idx |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 953.96 | 897.83 | 927.32 | 620.24 | 0.53 | shared hit=12225, temp read=1441 written=1445 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_evaluated_at_brin (filter removed 4,053) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 1.67 | 0.71 | 0.99 | 0.07 | 0.41 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 0.91 | 0.88 | 1.01 | 0.12 | 0.61 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 1.42 | 0.77 | 1.14 | 0.06 | 0.39 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |
| proposal_ops_list_month_old_by_time | PLATFORM | 51 | 390.80 | 360.07 | 394.58 | 370.48 | 0.46 | shared hit=21331 | Gather Merge → Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_evaluated_at_brin |
| proposal_ops_list_company_month_old_by_time | PLATFORM | 51 | 4.76 | 1.48 | 1.64 | 0.23 | 0.46 | shared hit=33 | Sort Method: quicksort → Index Scan Backward using fuelings_company_time_idx |
| proposal_company_list_month_old_big_by_time | COMPANY | 51 | 1.77 | 1.53 | 1.92 | 0.34 | 0.46 | shared hit=33 | Sort Method: quicksort → Index Scan Backward using fuelings_company_time_idx |
| proposal_operator_list_month_old_by_time | OPERATOR | 51 | 1.72 | 1.60 | 1.81 | 0.35 | 0.45 | shared hit=37 | Sort Method: quicksort → Index Scan Backward using fuelings_operator_time_idx |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 0.966 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 5863 ms.

