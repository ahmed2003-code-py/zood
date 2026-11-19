# Query plan report

Generated 2026-11-19T00:27:22.963751100Z by QueryPlanReport.

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
| fuelings | 5,000,000 | 4052 MB |
| alerts | 50,007 | 17 MB |

- fuelings heap only: 1890 MB; time span 2025-11-18 03:09:29.01295+02 .. 2026-11-19 02:14:58+02
- status/origin mix: COMPLETED/OFFLINE=475246, COMPLETED/ONLINE=4274486, DECLINED/ONLINE=250268
- largest company: 19869 vehicles, 496,504 fuelings; median company: 1404 vehicles, 35,365 fuelings
- operator used: 249,886 fuelings; station used: 16,926 fuelings
- windows: old month 2026-05, last full month 2026-10, dataset end 2026-11-19T00:14:58Z (all months in Africa/Cairo)
- load time: 748 s (the fuelings same-tenant trigger disabled during the bulk insert, then re-enabled; foreign keys and CHECK constraints enforced; same-tenant rule verified set-based afterwards)
- existing fuelings indexes: fuelings_client_txn_id_key 193 MB; fuelings_company_id_idx 428 MB; fuelings_ledger_transaction_key 8192 bytes; fuelings_offline_conflicts_idx 7936 kB; fuelings_offline_exposure_idx 34 MB; fuelings_operator_id_idx 420 MB; fuelings_payable_held_idx 8192 bytes; fuelings_pkey 150 MB; fuelings_reference_key 260 MB; fuelings_station_id_idx 426 MB; fuelings_vehicle_usage_idx 244 MB


## Phase `baseline`

Indexes: the V1–V11 indexes only.

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 25.66 | 4.07 | 6.61 | 0.09 | 0.29 | shared hit=6 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 1.95 | 1.58 | 1.71 | 0.08 | 0.30 | shared hit=6 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 3406.93 | 1611.48 | 1820.95 | 2505.97 | 0.43 | shared hit=2813 read=121328 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 1633.57 | 1619.12 | 1636.88 | 1627.98 | 0.40 | shared hit=2298 read=122304 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 1.63 | 1.50 | 1.63 | 0.31 | 0.33 | shared hit=26 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 1.67 | 1.52 | 1.84 | 0.27 | 0.31 | shared hit=26 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 181.34 | 65.54 | 73.99 | 57.78 | 0.46 | shared hit=13107 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 1516.78 | 1619.97 | 1644.16 | 1554.78 | 0.43 | shared hit=15170 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 127.75 | 14.00 | 15.73 | 14.40 | 0.38 | shared hit=15858 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 2.38 | 1.32 | 1.49 | 0.20 | 0.27 | shared hit=53 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 453.69 | 63.06 | 70.49 | 64.21 | 0.33 | shared hit=19282 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_station_id_idx → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 15,541) |
| operator_list_newest | OPERATOR | 51 | 1.55 | 1.67 | 7.54 | 0.32 | 0.22 | shared hit=55 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 1447.94 | 1620.36 | 1632.87 | 1598.35 | 0.43 | shared hit=5527 read=119191 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 3023.75 | 4180.49 | 4237.20 | 4224.10 | 0.40 | shared hit=60682 read=181270 | GroupAggregate → Gather Merge → Sort Method: quicksort → Parallel Seq Scan on fuelings (filter removed 1,652,697) |
| activity_company_month_median | COMPANY | 31 | 481.12 | 71.30 | 77.70 | 70.09 | 0.31 | shared hit=33522 | GroupAggregate → Gather Merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_id_idx (filter removed 10,818) |
| activity_operator_month | OPERATOR | 31 | 4404.05 | 748.19 | 795.76 | 776.90 | 0.66 | shared hit=49 read=160901 | GroupAggregate → Gather Merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 76,250) |
| activity_platform_month | PLATFORM | 31 | 4003.83 | 1039.14 | 1098.84 | 1064.32 | 0.34 | shared hit=64966 read=176986, temp read=3452 written=3460 | GroupAggregate → Gather Merge → Sort Method: external merge → Parallel Seq Scan on fuelings (filter removed 1,525,097) |
| activity_alerts_company_month | COMPANY | 31 | 45.67 | 4.88 | 4.97 | 3.08 | 0.27 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 18.11 | 14.26 | 15.54 | 11.33 | 0.50 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 685.11 | 630.30 | 656.13 | 635.78 | 0.45 | shared hit=63639 read=178237 | Gather → Parallel Seq Scan on fuelings (filter removed 1,652,686) |
| export_company_month_rows | COMPANY | 41942 | 856.94 | 841.66 | 854.24 | 651.59 | 0.29 | shared hit=64935 read=177033 | Gather Merge → Sort Method: quicksort → Parallel Seq Scan on fuelings (filter removed 1,652,686) |
| export_operator_month_count | OPERATOR | 1 | 3475.39 | 748.69 | 760.83 | 729.90 | 0.29 | shared hit=40 read=160894 | Gather → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 76,205) |
| export_operator_month_rows | OPERATOR | 21271 | 841.41 | 828.71 | 837.77 | 756.48 | 0.40 | shared hit=64 read=160902 | Gather Merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_operator_id_idx (filter removed 76,205) |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 4471.30 | 1158.59 | 1192.74 | 949.94 | 0.31 | shared hit=64969 read=176981, temp read=1440 written=1443 | GroupAggregate → Gather Merge → Sort Method: external merge → Parallel Seq Scan on fuelings (filter removed 1,588,327) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 2.46 | 0.63 | 0.68 | 0.05 | 0.33 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 0.77 | 0.60 | 0.88 | 0.05 | 0.45 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 1.88 | 0.66 | 0.83 | 0.09 | 0.42 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |

Generic plans (what PostgreSQL would run if it switched a prepared statement to its generic plan; parameters unknown at plan time):

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 2492.79 | 2457.67 | 2527.98 | 2669.28 | 0.01 | shared hit=64863 read=177013 | Sort Method: top-N heapsort → Gather → Parallel Seq Scan on fuelings |
| company_list_month_old_big | COMPANY | 51 | 654.88 | 652.78 | 669.54 | 720.20 | 0.02 | shared hit=64899 read=176977 | Sort Method: top-N heapsort → Gather → Parallel Seq Scan on fuelings (filter removed 1,652,697) |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 0.497 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 3061 ms.

## Phase `brin`

- built `fuelings_perf_evaluated_at_brin` in 5.9 s (plain CREATE INDEX)

| Added index | Size | Valid |
|---|---:|---|
| `fuelings_perf_evaluated_at_brin` | 80 kB | true |

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 19.58 | 1.42 | 1.89 | 0.18 | 0.26 | shared hit=82 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 1.14 | 1.03 | 1.72 | 0.17 | 0.25 | shared hit=82 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 4014.45 | 1623.75 | 1633.25 | 2364.74 | 0.42 | shared hit=2426 read=121791 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 1636.66 | 1647.69 | 1660.26 | 1596.33 | 0.42 | shared hit=2298 read=122380 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 4.11 | 1.50 | 2.06 | 0.43 | 0.63 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 1.76 | 1.63 | 1.83 | 0.34 | 0.33 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 176.58 | 64.34 | 70.13 | 67.34 | 0.28 | shared hit=13183 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 1508.62 | 1618.36 | 1630.26 | 1505.30 | 0.38 | shared hit=15246 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 173.81 | 15.08 | 15.71 | 15.25 | 0.36 | shared hit=15860 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 4.31 | 1.21 | 1.37 | 0.27 | 0.35 | shared hit=54 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 89.16 | 15.19 | 17.08 | 14.46 | 0.30 | shared hit=1593 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin → Bitmap Index Scan on fuelings_station_id_idx |
| operator_list_newest | OPERATOR | 51 | 2.65 | 1.28 | 2.27 | 0.70 | 0.61 | shared hit=131 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 1484.86 | 1622.18 | 1635.44 | 1557.01 | 0.37 | shared hit=11095 read=113699 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 1539.93 | 305.45 | 309.21 | 294.23 | 0.41 | shared hit=24200 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin → Bitmap Index Scan on fuelings_company_id_idx |
| activity_company_month_median | COMPANY | 31 | 87.75 | 32.23 | 34.71 | 32.39 | 0.31 | shared hit=3239 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin → Bitmap Index Scan on fuelings_company_id_idx |
| activity_operator_month | OPERATOR | 31 | 771.12 | 188.97 | 199.70 | 191.04 | 0.32 | shared hit=16635 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin → Bitmap Index Scan on fuelings_operator_id_idx |
| activity_platform_month | PLATFORM | 31 | 558.59 | 549.86 | 564.12 | 568.57 | 0.38 | shared hit=21315, temp read=3452 written=3461 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin |
| activity_alerts_company_month | COMPANY | 31 | 47.22 | 3.56 | 4.85 | 4.45 | 0.26 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 13.78 | 13.59 | 13.98 | 12.22 | 0.24 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 274.90 | 252.47 | 267.41 | 255.55 | 0.27 | shared hit=24379 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin → Bitmap Index Scan on fuelings_company_id_idx |
| export_company_month_rows | COMPANY | 41942 | 525.57 | 477.51 | 490.57 | 325.36 | 0.29 | shared hit=24379, temp read=1014 written=1016 | Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin → Bitmap Index Scan on fuelings_company_id_idx |
| export_operator_month_count | OPERATOR | 1 | 218.31 | 160.76 | 176.00 | 162.44 | 0.32 | shared hit=16772 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin → Bitmap Index Scan on fuelings_operator_id_idx |
| export_operator_month_rows | OPERATOR | 21271 | 283.46 | 281.72 | 286.91 | 189.66 | 0.38 | shared hit=16772, temp read=521 written=522 | Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin → Bitmap Index Scan on fuelings_operator_id_idx |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 596.51 | 634.25 | 660.35 | 447.25 | 0.32 | shared hit=12225, temp read=1441 written=1444 | GroupAggregate → Gather Merge → Sort Method: external merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin (filter removed 4,053) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 2.89 | 0.31 | 0.61 | 0.04 | 0.25 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 0.67 | 0.53 | 0.62 | 0.04 | 0.40 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 1.09 | 0.34 | 0.35 | 0.04 | 0.25 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 0.482 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 3238 ms.

## Phase `time`

- built `fuelings_perf_evaluated_at_idx` in 5.0 s (plain CREATE INDEX)

| Added index | Size | Valid |
|---|---:|---|
| `fuelings_perf_evaluated_at_idx` | 107 MB | true |

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 21.84 | 0.76 | 0.88 | 0.17 | 0.23 | shared hit=82 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 1.32 | 0.91 | 1.20 | 0.14 | 0.26 | shared hit=82 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 2201.73 | 1608.29 | 1656.04 | 3130.85 | 0.28 | shared hit=2356 read=121861 written=778 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 1673.73 | 1619.44 | 1658.57 | 1605.62 | 0.39 | shared hit=2298 read=122380 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 3.24 | 1.32 | 1.61 | 0.42 | 0.32 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 1.34 | 1.40 | 1.48 | 0.30 | 0.27 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 171.66 | 60.86 | 62.13 | 57.02 | 0.29 | shared hit=13183 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 1516.17 | 1603.90 | 1631.48 | 1487.34 | 0.29 | shared hit=15246 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 180.73 | 16.00 | 16.74 | 13.18 | 0.36 | shared hit=15860 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 4.20 | 1.06 | 1.80 | 0.19 | 0.27 | shared hit=54 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 137.42 | 25.80 | 27.58 | 24.27 | 0.30 | shared hit=2696 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_station_id_idx → Bitmap Index Scan on fuelings_perf_evaluated_at_idx |
| operator_list_newest | OPERATOR | 51 | 3.25 | 1.51 | 1.56 | 0.58 | 0.36 | shared hit=131 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 1500.26 | 1613.99 | 1634.10 | 1515.36 | 0.38 | shared hit=11092 read=113702 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 1264.57 | 152.78 | 208.34 | 181.46 | 0.40 | shared hit=99388 | HashAggregate → Gather → Parallel Index Scan using fuelings_perf_evaluated_at_idx (filter removed 127,600) |
| activity_company_month_median | COMPANY | 31 | 84.40 | 40.17 | 41.47 | 38.56 | 0.42 | shared hit=4289 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_company_id_idx → Bitmap Index Scan on fuelings_perf_evaluated_at_idx |
| activity_operator_month | OPERATOR | 31 | 119.70 | 149.48 | 174.45 | 124.30 | 0.56 | shared hit=99431 | HashAggregate → Gather → Parallel Index Scan using fuelings_perf_evaluated_at_idx (filter removed 134,524) |
| activity_platform_month | PLATFORM | 31 | 556.45 | 566.55 | 592.56 | 627.68 | 0.37 | shared hit=99496, temp read=3452 written=3461 | GroupAggregate → Gather Merge → Sort Method: external merge → Parallel Index Scan using fuelings_perf_evaluated_at_idx |
| activity_alerts_company_month | COMPANY | 31 | 48.04 | 4.69 | 5.46 | 4.72 | 0.27 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 13.63 | 10.79 | 14.24 | 13.86 | 0.23 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 252.77 | 159.59 | 171.00 | 131.19 | 0.33 | shared hit=98552 | Gather → Parallel Index Scan using fuelings_perf_evaluated_at_idx (filter removed 127,572) |
| export_company_month_rows | COMPANY | 41942 | 358.81 | 340.78 | 352.23 | 180.17 | 0.29 | shared hit=98839 | Gather Merge → Sort Method: quicksort → Parallel Index Scan using fuelings_perf_evaluated_at_idx (filter removed 127,572) |
| export_operator_month_count | OPERATOR | 1 | 112.18 | 112.27 | 118.37 | 144.78 | 0.26 | shared hit=98465 | Gather → Parallel Index Scan using fuelings_perf_evaluated_at_idx (filter removed 134,462) |
| export_operator_month_rows | OPERATOR | 21271 | 241.20 | 238.34 | 347.25 | 159.26 | 0.29 | shared hit=98826 | Gather Merge → Sort Method: quicksort → Parallel Index Scan using fuelings_perf_evaluated_at_idx (filter removed 134,462) |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 715.95 | 610.11 | 669.65 | 427.89 | 0.42 | shared hit=56612, temp read=1440 written=1443 | GroupAggregate → Gather Merge → Sort Method: external merge → Parallel Index Scan using fuelings_perf_evaluated_at_idx (filter removed 4,053) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 4.82 | 0.42 | 0.47 | 0.04 | 0.35 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 0.83 | 0.52 | 1.15 | 0.04 | 0.50 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 4.52 | 0.33 | 0.72 | 0.04 | 0.30 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 0.492 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 3337 ms.

## Phase `composite`

- built `fuelings_perf_company_time_idx` in 7.7 s (plain CREATE INDEX)
- built `fuelings_perf_operator_time_idx` in 9.9 s (plain CREATE INDEX)
- built `fuelings_perf_station_time_idx` in 9.8 s (plain CREATE INDEX)

| Added index | Size | Valid |
|---|---:|---|
| `fuelings_perf_company_time_idx` | 194 MB | true |
| `fuelings_perf_operator_time_idx` | 194 MB | true |
| `fuelings_perf_station_time_idx` | 194 MB | true |

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 20.87 | 0.83 | 0.89 | 0.17 | 0.24 | shared hit=82 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 1.59 | 0.96 | 1.15 | 0.16 | 0.27 | shared hit=82 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 4633.29 | 1602.21 | 1647.58 | 3000.02 | 0.32 | shared hit=2373 read=121844 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 1670.12 | 1618.99 | 1634.59 | 1590.50 | 0.65 | shared hit=2298 read=122380 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 2.92 | 1.43 | 1.53 | 0.41 | 0.36 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 1.65 | 1.59 | 2.30 | 0.43 | 0.39 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 178.75 | 62.81 | 73.04 | 65.76 | 0.40 | shared hit=13183 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 1604.10 | 1614.53 | 1650.28 | 1498.38 | 0.30 | shared hit=15246 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 186.67 | 13.03 | 15.14 | 11.73 | 0.64 | shared hit=15860 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 4.50 | 1.33 | 1.84 | 0.25 | 0.38 | shared hit=54 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 113.69 | 12.33 | 15.91 | 10.61 | 0.33 | shared hit=1639 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx → Bitmap Index Scan on fuelings_station_id_idx |
| operator_list_newest | OPERATOR | 51 | 2.95 | 1.12 | 1.25 | 0.48 | 0.31 | shared hit=131 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 1494.02 | 1607.92 | 1644.10 | 1518.98 | 0.31 | shared hit=11092 read=113702 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 549.72 | 159.87 | 178.68 | 188.95 | 0.44 | shared hit=18404 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| activity_company_month_median | COMPANY | 31 | 26.75 | 13.59 | 14.40 | 12.43 | 0.33 | shared hit=2753 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| activity_operator_month | OPERATOR | 31 | 131.39 | 88.54 | 95.96 | 89.99 | 0.32 | shared hit=13565 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| activity_platform_month | PLATFORM | 31 | 3328.20 | 4676.68 | 4737.87 | 4533.50 | 0.32 | shared hit=61869 read=189650, temp read=3452 written=3461 | GroupAggregate → Gather Merge → Sort Method: external merge → Parallel Seq Scan on fuelings (filter removed 1,525,097) |
| activity_alerts_company_month | COMPANY | 31 | 48.86 | 3.38 | 4.53 | 3.06 | 0.22 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 13.44 | 11.69 | 12.37 | 10.78 | 0.20 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 151.01 | 110.18 | 115.75 | 111.64 | 0.27 | shared hit=18466 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| export_company_month_rows | COMPANY | 41942 | 343.57 | 293.67 | 338.76 | 121.81 | 0.33 | shared hit=18538, temp read=521 written=522 | Gather Merge → Sort Method: external merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| export_operator_month_count | OPERATOR | 1 | 106.41 | 67.41 | 73.54 | 66.19 | 0.35 | shared hit=13612 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| export_operator_month_rows | OPERATOR | 21271 | 192.88 | 186.21 | 196.66 | 100.80 | 0.41 | shared hit=13612, temp read=521 written=522 | Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 759.15 | 605.35 | 622.70 | 424.08 | 0.48 | shared hit=13343, temp read=1440 written=1443 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx (filter removed 4,053) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 2.82 | 0.46 | 0.78 | 0.05 | 0.35 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 0.90 | 0.63 | 1.38 | 0.04 | 0.47 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 1.32 | 0.36 | 0.55 | 0.05 | 0.38 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 0.580 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 3698 ms.

## Phase `composite_brin`

- built `fuelings_perf_company_time_idx` in 12.5 s (plain CREATE INDEX)
- built `fuelings_perf_operator_time_idx` in 10.5 s (plain CREATE INDEX)
- built `fuelings_perf_station_time_idx` in 9.5 s (plain CREATE INDEX)
- built `fuelings_perf_evaluated_at_brin` in 5.4 s (plain CREATE INDEX)

| Added index | Size | Valid |
|---|---:|---|
| `fuelings_perf_company_time_idx` | 194 MB | true |
| `fuelings_perf_operator_time_idx` | 194 MB | true |
| `fuelings_perf_station_time_idx` | 194 MB | true |
| `fuelings_perf_evaluated_at_brin` | 80 kB | true |

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 23.50 | 0.93 | 1.34 | 0.15 | 0.24 | shared hit=82 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 1.30 | 0.87 | 1.21 | 0.14 | 0.27 | shared hit=82 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 2097.17 | 1619.90 | 1638.58 | 2657.00 | 0.73 | shared hit=2373 read=121844 written=641 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 1631.61 | 1617.55 | 1656.43 | 1594.72 | 0.42 | shared hit=2298 read=122380 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 3.01 | 1.41 | 1.56 | 0.59 | 0.50 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 1.39 | 1.34 | 1.69 | 0.43 | 0.32 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 173.91 | 66.05 | 78.06 | 64.97 | 0.56 | shared hit=13183 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 1542.34 | 1614.70 | 1672.83 | 1468.20 | 0.31 | shared hit=15246 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 160.98 | 14.47 | 16.00 | 13.69 | 0.39 | shared hit=15860 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 5.32 | 1.10 | 1.20 | 0.20 | 0.29 | shared hit=54 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 54.53 | 12.74 | 13.86 | 10.31 | 0.32 | shared hit=1639 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx → Bitmap Index Scan on fuelings_station_id_idx |
| operator_list_newest | OPERATOR | 51 | 2.45 | 1.17 | 1.49 | 0.40 | 0.27 | shared hit=131 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 1472.00 | 1620.61 | 1652.77 | 1517.16 | 0.31 | shared hit=11091 read=113703 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 215.57 | 163.02 | 170.16 | 171.62 | 0.45 | shared hit=18404 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| activity_company_month_median | COMPANY | 31 | 28.11 | 14.35 | 14.63 | 14.08 | 0.45 | shared hit=2753 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| activity_operator_month | OPERATOR | 31 | 137.38 | 91.73 | 93.22 | 95.26 | 0.41 | shared hit=13565 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| activity_platform_month | PLATFORM | 31 | 565.27 | 542.28 | 628.91 | 581.47 | 0.33 | shared hit=21315, temp read=3451 written=3460 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin |
| activity_alerts_company_month | COMPANY | 31 | 48.99 | 4.61 | 4.66 | 4.37 | 0.27 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 11.93 | 13.47 | 14.10 | 13.75 | 0.24 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 158.04 | 110.03 | 123.53 | 129.49 | 0.35 | shared hit=18466 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| export_company_month_rows | COMPANY | 41942 | 348.37 | 314.84 | 355.04 | 124.89 | 0.37 | shared hit=18538, temp read=488 written=489 | Gather Merge → Sort Method: external merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| export_operator_month_count | OPERATOR | 1 | 117.89 | 65.29 | 71.87 | 60.61 | 0.27 | shared hit=13612 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| export_operator_month_rows | OPERATOR | 21271 | 199.25 | 176.51 | 190.39 | 102.80 | 0.42 | shared hit=13612, temp read=521 written=522 | Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 638.66 | 606.37 | 671.93 | 449.01 | 0.42 | shared hit=12225, temp read=1439 written=1442 | GroupAggregate → Gather Merge → Sort Method: external merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin (filter removed 4,053) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 1.33 | 0.34 | 0.83 | 0.04 | 0.28 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 0.64 | 0.43 | 0.92 | 0.03 | 0.34 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 1.03 | 0.46 | 0.83 | 0.04 | 0.34 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 0.536 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 3718 ms.

## Phase `company_operator_brin`

- built `fuelings_perf_company_time_idx` in 8.5 s (plain CREATE INDEX)
- built `fuelings_perf_operator_time_idx` in 10.7 s (plain CREATE INDEX)
- built `fuelings_perf_evaluated_at_brin` in 5.9 s (plain CREATE INDEX)

| Added index | Size | Valid |
|---|---:|---|
| `fuelings_perf_company_time_idx` | 194 MB | true |
| `fuelings_perf_operator_time_idx` | 194 MB | true |
| `fuelings_perf_evaluated_at_brin` | 80 kB | true |

| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| ops_list_newest | PLATFORM | 51 | 23.62 | 0.92 | 1.10 | 0.17 | 0.24 | shared hit=82 | Index Scan Backward using fuelings_pkey |
| ops_list_last30 | PLATFORM | 51 | 1.48 | 0.96 | 2.10 | 0.15 | 0.26 | shared hit=82 | Index Scan Backward using fuelings_pkey (filter removed 1) |
| ops_list_month_old | PLATFORM | 51 | 2240.95 | 1593.54 | 1616.71 | 2339.20 | 0.55 | shared hit=2293 read=121924 written=530 | Index Scan Backward using fuelings_pkey (filter removed 2,333,369) |
| ops_list_company_month_old | PLATFORM | 51 | 1622.90 | 1621.38 | 1648.56 | 1661.83 | 0.33 | shared hit=2298 read=122380 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_newest_big | COMPANY | 51 | 2.58 | 1.05 | 1.74 | 0.39 | 0.33 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last30_big | COMPANY | 51 | 1.71 | 1.58 | 1.91 | 0.29 | 0.26 | shared hit=102 | Index Scan Backward using fuelings_pkey (filter removed 383) |
| company_list_last_month_big | COMPANY | 51 | 167.52 | 63.62 | 71.37 | 63.37 | 0.37 | shared hit=13183 | Index Scan Backward using fuelings_pkey (filter removed 246,477) |
| company_list_month_old_big | COMPANY | 51 | 1504.10 | 1637.53 | 1677.15 | 1537.14 | 0.30 | shared hit=15246 read=109432 | Index Scan Backward using fuelings_pkey (filter removed 2,342,035) |
| company_list_month_old_median | COMPANY | 51 | 155.44 | 14.57 | 16.00 | 12.60 | 0.30 | shared hit=15860 | Index Scan Backward using fuelings_company_id_idx (filter removed 16,720) |
| station_list_newest | OPERATOR | 51 | 5.20 | 0.93 | 1.49 | 0.20 | 0.37 | shared hit=54 | Index Scan Backward using fuelings_station_id_idx |
| station_list_month_old | OPERATOR | 51 | 53.20 | 12.33 | 13.39 | 10.26 | 0.33 | shared hit=1639 | Sort Method: top-N heapsort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx → Bitmap Index Scan on fuelings_station_id_idx |
| operator_list_newest | OPERATOR | 51 | 2.55 | 1.52 | 2.15 | 0.38 | 0.25 | shared hit=131 | Index Scan Backward using fuelings_pkey (filter removed 907) |
| operator_list_month_old | OPERATOR | 51 | 1483.90 | 1610.38 | 1627.29 | 1554.45 | 0.38 | shared hit=11092 read=113702 | Index Scan Backward using fuelings_pkey (filter removed 2,344,238) |
| activity_company_month_big | COMPANY | 31 | 209.83 | 160.95 | 176.07 | 178.14 | 0.36 | shared hit=18404 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| activity_company_month_median | COMPANY | 31 | 25.14 | 14.08 | 16.47 | 13.20 | 0.33 | shared hit=2753 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| activity_operator_month | OPERATOR | 31 | 156.67 | 94.11 | 100.10 | 101.82 | 0.39 | shared hit=13565 | HashAggregate → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| activity_platform_month | PLATFORM | 31 | 581.34 | 537.90 | 565.38 | 629.94 | 0.32 | shared hit=21315, temp read=3451 written=3459 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin |
| activity_alerts_company_month | COMPANY | 31 | 56.45 | 4.67 | 5.05 | 4.28 | 0.27 | shared hit=1203 | GroupAggregate → Sort Method: quicksort → Bitmap Heap Scan on alerts → Bitmap Index Scan on alerts_company_id_idx (filter removed 4,544) |
| activity_alerts_platform_month | PLATFORM | 31 | 14.00 | 12.94 | 14.40 | 10.22 | 0.25 | shared hit=1163 | HashAggregate → Seq Scan on alerts (filter removed 45,626) |
| export_company_month_count | COMPANY | 1 | 164.82 | 118.26 | 128.63 | 126.17 | 0.35 | shared hit=18466 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| export_company_month_rows | COMPANY | 41942 | 376.04 | 304.64 | 314.72 | 125.31 | 0.41 | shared hit=18538, temp read=473 written=474 | Gather Merge → Sort Method: external merge → Sort Method: quicksort → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_company_time_idx |
| export_operator_month_count | OPERATOR | 1 | 113.74 | 68.58 | 75.03 | 63.95 | 0.27 | shared hit=13612 | Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| export_operator_month_rows | OPERATOR | 21271 | 183.73 | 187.85 | 204.55 | 106.78 | 0.35 | shared hit=13612, temp read=521 written=522 | Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_operator_time_idx |
| usage_snapshot_since_period_starts | PLATFORM | 138316 | 643.84 | 628.46 | 654.46 | 423.51 | 0.41 | shared hit=12225, temp read=1441 written=1444 | GroupAggregate → Gather Merge → Sort Method: external merge → Bitmap Heap Scan on fuelings → Bitmap Index Scan on fuelings_perf_evaluated_at_brin (filter removed 4,053) |
| engine_vehicle_volume_in_month | PLATFORM | 1 | 1.28 | 0.41 | 0.47 | 0.05 | 0.29 | shared hit=6 | Index Scan using fuelings_vehicle_usage_idx |
| engine_latest_completed_between | PLATFORM | 1 | 0.66 | 0.46 | 0.48 | 0.03 | 0.37 | shared hit=4 | Index Scan Backward using fuelings_vehicle_usage_idx |
| engine_offline_amount_since_24h | PLATFORM | 1 | 1.07 | 0.34 | 0.35 | 0.04 | 0.25 | shared hit=5 | Index Scan using fuelings_offline_exposure_idx |

Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert 0.536 ms/row (2,000 inserts in one transaction); bulk insert of 20,000 rows 3720 ms.

