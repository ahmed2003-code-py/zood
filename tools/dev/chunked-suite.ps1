# Runs the API test suite one package at a time, retrying a package only when its failures are the
# known environmental ones (embedded PostgreSQL port/connection contention on this machine).
$api = 'C:\Users\ae11w\OneDrive\Desktop\Zood\apps\api'
$log = 'C:\Users\ae11w\AppData\Local\Temp\claude\7b85643f2004186f\a8008b01-f582-47d7-92c9-f8bbcc1da429\scratchpad\gate-b7-chunked.log'
$envPattern = 'Address already in use|connection attempt failed|Permission denied: connect|failure threshold \(1\) exceeded'
Set-Location $api
"" | Out-File -Encoding utf8 $log
$groups = @('com.zawed.api.*Tests', 'com.zawed.api.fleet.**', 'com.zawed.api.fueling.**', 'com.zawed.api.identity.**',
  'com.zawed.api.ledger.**', 'com.zawed.api.network.**', 'com.zawed.api.platform.**', 'com.zawed.api.pricing.**',
  'com.zawed.api.rules.**', 'com.zawed.api.stationfinance.**', 'com.zawed.api.tenancy.**', 'com.zawed.api.tickets.**',
  'com.zawed.api.sync.**', 'com.zawed.api.audit.**', 'com.zawed.api.perf.**')
$total = 0; $failed = @()
foreach ($g in $groups) {
  for ($i = 1; $i -le 4; $i++) {
    Remove-Item target\surefire-reports\*.txt -ErrorAction SilentlyContinue
    .\mvnw.cmd -B -q test "-Dtest=$g" "-Dsurefire.failIfNoSpecifiedTests=false" *> "$log.part"
    $code = $LASTEXITCODE
    $reports = Get-ChildItem target\surefire-reports\*.txt -ErrorAction SilentlyContinue
    $runs = 0; foreach ($r in $reports) { $m = Select-String -Path $r.FullName -Pattern 'Tests run: (\d+)' | Select-Object -First 1; if ($m) { $runs += [int]$m.Matches[0].Groups[1].Value } }
    if ($code -eq 0) { break }
    $bad = $reports | Where-Object { (Select-String -Path $_.FullName -Pattern '<<< (FAILURE|ERROR)' -Quiet) -and -not (Select-String -Path $_.FullName -Pattern $envPattern -Quiet) }
    if ($bad) { break }
  }
  "{0,-34} attempts={1} exit={2} tests={3}" -f $g, $i, $code, $runs | Out-File -Append -Encoding utf8 $log
  $total += $runs
  if ($code -ne 0) { $failed += $g; Get-Content "$log.part" | Select-String '<<< (FAIL|ERR)' | Select-Object -First 5 | Out-File -Append -Encoding utf8 $log }
}
"TOTAL_TESTS=$total FAILED_GROUPS=$($failed -join ',')" | Out-File -Append -Encoding utf8 $log
