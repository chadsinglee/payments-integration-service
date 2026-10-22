<#
.SYNOPSIS
  Live console walkthrough of the payments flow against the running Compose stack.

.DESCRIPTION
  Submits real payments to the Api, watches the worker move them through their states in real time,
  then shows idempotency, declines, a 429 retry, an unknown outcome resolved by RTTQ, duplicate callbacks,
  rejected callbacks and the daily summary. Every step prints what it expects and PASS/FAIL.

  Needs: `docker compose up --build -d` already running (Api :5001, stub :5002).
  Docker CLI access to the postgres container is optional; without it the timeline and callback steps are skipped.
  Run one copy at a time: the summary check compares totals before and after, so other traffic on the same sub-merchant skews it.
  Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  ./ai/demo.ps1
  ./ai/demo.ps1 -Pause      # wait for Enter between steps while presenting
  ./ai/demo.ps1 -SkipSlow   # skip the 35 s unknown-outcome payment
#>
[CmdletBinding()]
param(
    [string]$Api = 'http://localhost:5001',
    [string]$Stub = 'http://localhost:5002',
    [string]$SubMerchant = 'ZAP0004182',
    [string]$Customer = 'cust-001',
    [switch]$Pause,
    [switch]$SkipSlow
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$script:Http = [System.Net.Http.HttpClient]::new()
$script:Http.Timeout = [TimeSpan]::FromSeconds(60)
$script:Pass = 0
$script:Fail = 0
$run = Get-Date -Format 'HHmmss'
$stepNumber = 0

function Write-Step([string]$title, [string]$why) {
    $script:stepNumber++
    if ($Pause -and $script:stepNumber -gt 1) { [void](Read-Host "`n(press Enter for the next step)") }
    Write-Host ""
    Write-Host ("=" * 78) -ForegroundColor DarkGray
    Write-Host ("STEP {0}: {1}" -f $script:stepNumber, $title) -ForegroundColor Cyan
    Write-Host ("WHY : {0}" -f $why) -ForegroundColor DarkGray
    Write-Host ("=" * 78) -ForegroundColor DarkGray
}

function Write-Info([string]$text) { Write-Host "  $text" }

function Assert-That([string]$what, [bool]$condition, [string]$detail = '') {
    if ($condition) {
        $script:Pass++
        Write-Host ("  PASS  {0}" -f $what) -ForegroundColor Green
    }
    else {
        $script:Fail++
        Write-Host ("  FAIL  {0} {1}" -f $what, $detail) -ForegroundColor Red
    }
}

function Send-Http([string]$Method, [string]$Url, [string]$Body = $null, [hashtable]$Headers = @{}) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    if ($Body) { $request.Content = [System.Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, 'application/json') }
    foreach ($key in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key, $Headers[$key]) }
    $response = $script:Http.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $location = $null
    if ($response.Headers.Location) { $location = $response.Headers.Location.ToString() }
    [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $text; Location = $location }
}

function Submit-Payment([string]$id, [long]$amount, [string]$customer = $Customer, [string]$subMerchant = $SubMerchant) {
    $json = @{ paymentId = $id; customerId = $customer; subMerchantId = $subMerchant; amount = $amount } | ConvertTo-Json -Compress
    Write-Info "POST /payments  $json"
    Send-Http 'POST' "$Api/payments" $json
}

function Get-Payment([string]$id) {
    $r = Send-Http 'GET' "$Api/payments/$id"
    if ($r.Status -eq 200) { $r.Body | ConvertFrom-Json } else { $null }
}

# Polls once a second and prints each state change, so the worker's progress is visible live.
function Wait-Payment([string]$id, [string[]]$until, [int]$timeoutSeconds = 30) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $last = ''
    while ($clock.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        $p = Get-Payment $id
        if ($p -and $p.status -ne $last) {
            $id, $p.status
            $last = $p.status
        }
        if ($p -and ($until -contains $p.status)) { return $p }
        Start-Sleep -Milliseconds 500
    }
    Get-Payment $id
}

$script:PostgresContainer = $null
function Find-Postgres {
    try {
        $name = (& docker ps --filter 'name=postgres' --format '{{.Names}}' 2>$null | Select-Object -First 1)
        if ($name) { $script:PostgresContainer = $name }
    }
    catch { }
}

function Invoke-Sql([string]$sql) {
    if (-not $script:PostgresContainer) { return $null }
    $sql | & docker exec -i $script:PostgresContainer psql -U payments -d payments -At -F '|' 2>$null
}

function Show-Timeline([string]$id) {
    $rows = Invoke-Sql @"
SELECT to_char(e.received_at AT TIME ZONE 'Africa/Johannesburg', 'HH24:MI:SS.MS'),
       CASE e.source WHEN 1 THEN 'Acme POST response' WHEN 2 THEN 'Payment Advice' WHEN 3 THEN 'Webhook' WHEN 4 THEN 'RTTQ' WHEN 5 THEN 'System' END,
       CASE e.event_type WHEN 1 THEN 'Approved' WHEN 2 THEN 'OutcomeUnknown' WHEN 3 THEN 'Declined' WHEN 4 THEN 'Failed' WHEN 5 THEN 'Reversal' WHEN 6 THEN 'Refund' WHEN 7 THEN 'Informational' WHEN 8 THEN 'ReversalObserved' WHEN 9 THEN 'RefundObserved' END
FROM payment_events e JOIN payments p ON p.id = e.payment_id
WHERE p.app_payment_id = '$id' ORDER BY e.id;
"@
    $facts = Invoke-Sql "SELECT 'attempts=' || attempt_count || '  rttq_attempts=' || rttq_attempts || '  transaction_id=' || transaction_id || '  order_number=' || order_number FROM payments WHERE app_payment_id = '$id';"
    if ($null -eq $rows -and $null -eq $facts) { Write-Info '(timeline skipped: no docker access to postgres)'; return }
    Write-Info "Event log for $id (status is recomputed from these rows):"
    foreach ($row in @($rows)) { if ($row) { $c = $row -split '\|'; Write-Host ("    {0}  {1,-20} {2}" -f $c[0], $c[1], $c[2]) -ForegroundColor Gray } }
    foreach ($f in @($facts)) { if ($f) { Write-Host "    $f" -ForegroundColor Gray } }
}

# Proves a retry or resend reuses the TransactionID while every attempt carries a fresh MessageID.
function Show-StubPosts([string]$transactionId) {
    $posts = @((Invoke-RestMethod "$Stub/admin/posts") | ForEach-Object { $_ } | Where-Object { $_.transactionId -eq $transactionId })
    Write-Info "POSTs the Acme stub received for TransactionID ${transactionId}:"
    foreach ($post in $posts) { Write-Host ("    MessageID {0}  order {1}  amount {2}" -f $post.messageId, $post.orderNumber, $post.amount) -ForegroundColor Gray }
    $posts.Count
}

function Get-TransactionId([string]$id) {
    $t = Invoke-Sql "SELECT transaction_id FROM payments WHERE app_payment_id = '$id';"
    if ($t) { ([string]($t | Select-Object -First 1)).Trim() } else { $null }
}

function Get-Summary([string]$day) {
    $r = Send-Http 'GET' "$Api/summary?date=$day"
    $s = $r.Body | ConvertFrom-Json
    $row = @($s.rows) | Where-Object { $_.subMerchantId -eq $SubMerchant -and $_.currency -eq 'ZAR' }
    if (-not $row) {
        $zero = [pscustomobject]@{ count = 0; amountMinor = 0 }
        $row = [pscustomobject]@{ approved = $zero; declined = $zero; reversals = $zero; refunds = $zero }
    }
    [pscustomobject]@{ Envelope = $s; Row = $row }
}

# ---------------------------------------------------------------------------------------------
Write-Host "Payments demo, run id $run" -ForegroundColor White
Find-Postgres

Write-Step 'Check the stack is up' 'Everything below talks to the real Api, worker, Redis, Postgres and the Acme stub.'
$apiHealth = Send-Http 'GET' "$Api/health"
$stubHealth = Send-Http 'GET' "$Stub/health"
Assert-That "Api healthy at $Api" ($apiHealth.Status -eq 200)
Assert-That "Acme stub healthy at $Stub" ($stubHealth.Status -eq 200)
if ($script:PostgresContainer) { Write-Info "Postgres container: $script:PostgresContainer" } else { Write-Info 'Postgres container not reachable through docker: timeline and callback steps will be skipped.' }
if ($apiHealth.Status -ne 200 -or $stubHealth.Status -ne 200) { Write-Host "Start the stack first: docker compose up --build -d" -ForegroundColor Red; exit 2 }

$day = [DateTimeOffset]::UtcNow.AddHours(2).ToString('yyyy-MM-dd')
$before = Get-Summary $day

$slowId = "demo-$run-slow"
$slowStarted = $null
if (-not $SkipSlow) {
    Write-Step 'Start a slow payment in the background' 'Acme can hold a request for 30 s or more, so our client timeout is 40 s. This one takes 35 s and runs while we demo the rest: a slow Acme call blocks neither the Api nor other payments. We come back to it near the end.'
    $r = Submit-Payment $slowId 99035
    $slowStarted = Get-Date
    Assert-That 'accepted with 202 (the Api never waits on Acme)' ($r.Status -eq 202)
}

Write-Step 'Happy path: accept fast, process later' 'The Api only records the payment and answers 202. A worker claims it, takes a rate-limit token, calls Acme with a fixed TransactionID and records the outcome.'
$happyId = "demo-$run-happy"
$r = Submit-Payment $happyId 12500
Write-Info ("HTTP {0}   Location: {1}" -f $r.Status, $r.Location)
Assert-That '202 Accepted with a Location header' ($r.Status -eq 202 -and $r.Location -like "*$happyId*")
Write-Info 'Watching the worker pick it up:'
$p = Wait-Payment $happyId @('Approved', 'Declined', 'Failed') 30
Assert-That 'payment reached Approved' ($p.status -eq 'Approved') "(was $($p.status))"
Show-Timeline $happyId

Write-Step 'Idempotency and validation' 'A retry after a network blip must never create a second charge. A unique database constraint decides, so two simultaneous requests cannot both win.'
$r = Submit-Payment $happyId 12500
Assert-That 'same id + same details -> 200 with the existing payment' ($r.Status -eq 200)
$r = Submit-Payment $happyId 99999
Assert-That 'same id + different amount -> 409 Conflict' ($r.Status -eq 409)
$r = Submit-Payment "demo-$run-bad" 0
Assert-That 'amount 0 -> 400 validation problem' ($r.Status -eq 400)
$r = Submit-Payment "demo-$run-nosm" 5000 $Customer 'ZAP9999999'
Assert-That 'unknown sub-merchant -> 404' ($r.Status -eq 404)

Write-Step 'A decline is decided by the response code, not the status' 'Acme answers TransactionStatus=Success even for a decline; the code (51 = insufficient funds) is what matters.'
$declineId = "demo-$run-decline"
[void](Submit-Payment $declineId 99051)
$p = Wait-Payment $declineId @('Approved', 'Declined', 'Failed') 30
Assert-That 'payment ended Declined' ($p.status -eq 'Declined') "(was $($p.status))"
Show-Timeline $declineId

Write-Step 'Acme rate limit (429): back off, resend with the same TransactionID' 'A 429 means Acme processed nothing, so the payment goes back to Pending, the cooldown is shared via Redis, and it is retried with the same TransactionID.'
$limitedId = "demo-$run-429"
[void](Submit-Payment $limitedId 99429)
$p = Wait-Payment $limitedId @('Approved', 'Declined', 'Failed') 40
Assert-That 'payment eventually Approved after the 429' ($p.status -eq 'Approved') "(was $($p.status))"
Show-Timeline $limitedId
$limitedTx = Get-TransactionId $limitedId
if ($limitedTx) { [void](Show-StubPosts $limitedTx) }

Write-Step 'Unknown outcome: ask first, resend only on a definite "never saw it"' 'The first POST fails with an error we cannot read as success or decline, so the outcome is Unknown. The worker asks Acme (RTTQ) before doing anything. Only RTTQ 404 (Acme never saw it) allows a resend, and with the SAME TransactionID so Acme can recognise a repeat.'
$unknownId = "demo-$run-unknown"
[void](Submit-Payment $unknownId 99502)
$p = Wait-Payment $unknownId @('Approved', 'Declined', 'Failed') 40
Assert-That 'payment ended Approved after the resend' ($p.status -eq 'Approved') "(was $($p.status))"
Show-Timeline $unknownId
$unknownTx = Get-TransactionId $unknownId
if ($unknownTx) {
    $postCount = Show-StubPosts $unknownTx
    Assert-That 'Acme saw exactly 2 POSTs, both with the same TransactionID' ($postCount -eq 2) "(saw $postCount)"
    $attempts = [string](Invoke-Sql "SELECT attempt_count || ',' || rttq_attempts FROM payments WHERE app_payment_id = '$unknownId';" | Select-Object -First 1)
    Write-Info "attempt_count,rttq_attempts = $attempts"
    Assert-That 'the resend happened once (attempt_count 2) after one RTTQ check' ($attempts.Trim() -eq '2,1') "(was $attempts)"
}

Write-Step 'Duplicate and out-of-order callbacks' 'Acme sends notifications twice and in odd orders. Each is deduplicated by a unique key, and the status is recomputed from all events, so the result is the same either way.'
$refunded = $false
$transactionId = Get-TransactionId $happyId
if ($transactionId) {
    $script = 'refund:3000,advice,advice,refund:3000'
    Write-Info "Stub sends (with the real Basic auth / HMAC signature): $script"
    $r = Send-Http 'POST' "$Stub/admin/callbacks/${transactionId}?send=$script"
    Write-Info ("Api answered: {0}" -f $r.Body)
    $p = Wait-Payment $happyId @('PartiallyRefunded') 15
    $refunded = ($p.status -eq 'PartiallyRefunded')
    Assert-That 'status PartiallyRefunded (refund arrived before the advice, status still right)' $refunded "(was $($p.status))"
    Show-Timeline $happyId
    $refundEvents = @(Invoke-Sql "SELECT 1 FROM payment_events e JOIN payments p ON p.id = e.payment_id WHERE p.app_payment_id = '$happyId' AND e.event_type = 6;").Count
    Assert-That 'the refund was stored once although it was delivered twice' ($refundEvents -eq 1) "(stored $refundEvents)"
}
else { Write-Info '(skipped: needs docker access to look up the TransactionID)' }

Write-Step 'Callbacks must be authenticated before anything is read or stored' 'These endpoints are public. Advice uses Basic auth; the webhook uses an HMAC-SHA256 of the raw body, checked before parsing.'
$r = Send-Http 'POST' "$Api/acme/advice" '{"TransactionID":"00000000-0000-0000-0000-000000000000"}'
Assert-That 'advice without credentials -> 401' ($r.Status -eq 401)
$r = Send-Http 'POST' "$Api/acme/webhook" '{"transactionUUID":"x"}' @{ 'x-signature' = 'AAAA' }
Assert-That 'webhook with a wrong signature -> 401' ($r.Status -eq 401)

if (-not $SkipSlow) {
    Write-Step 'Back to the slow payment' 'Acme held this call for about 35 s, inside our 40 s client timeout, so the answer arrived normally: one attempt, no RTTQ. Everything else in this demo ran meanwhile.'
    $left = 100 - [int]((Get-Date) - $slowStarted).TotalSeconds
    Write-Info ("Waiting up to {0}s more..." -f [Math]::Max($left, 10))
    $p = Wait-Payment $slowId @('Approved', 'Declined', 'Failed') ([Math]::Max($left, 10))
    Assert-That 'slow payment Approved from the original POST response' ($p.status -eq 'Approved') "(was $($p.status))"
    Show-Timeline $slowId
}

Write-Step 'Daily summary (SAST day), checked against what this run did' 'Figures are per sub-merchant and currency on the SAST day [00:00, next 00:00). Gross: a partly refunded approval still counts as approved, the refund is shown on its own.'
Start-Sleep -Seconds 1
$after = Get-Summary $day
$d = {
    param($name)
    [pscustomobject]@{
        Count  = [long]$after.Row.$name.count - [long]$before.Row.$name.count
        Amount = [long]$after.Row.$name.amountMinor - [long]$before.Row.$name.amountMinor
    }
}
$approved = & $d 'approved'; $declined = & $d 'declined'; $refunds = & $d 'refunds'
$expectedApprovedCount = 3; $expectedApprovedAmount = 12500 + 99429 + 99502
if (-not $SkipSlow -and $p.status -eq 'Approved') { $expectedApprovedCount++; $expectedApprovedAmount += 99035 }
Write-Info ("Summary for {0} ({1}), settled={2}, cutoff {3}" -f $after.Envelope.date, $after.Envelope.timeZone, $after.Envelope.settled, $after.Envelope.cutoffAt)
Write-Host ("    {0,-10} {1,8} {2,12}" -f 'This run', 'count', 'cents') -ForegroundColor Gray
Write-Host ("    {0,-10} {1,8} {2,12}" -f 'approved', $approved.Count, $approved.Amount) -ForegroundColor Gray
Write-Host ("    {0,-10} {1,8} {2,12}" -f 'declined', $declined.Count, $declined.Amount) -ForegroundColor Gray
Write-Host ("    {0,-10} {1,8} {2,12}" -f 'refunds', $refunds.Count, $refunds.Amount) -ForegroundColor Gray
Assert-That "approved rose by $expectedApprovedCount payments / $expectedApprovedAmount cents" ($approved.Count -eq $expectedApprovedCount -and $approved.Amount -eq $expectedApprovedAmount)
Assert-That 'declined rose by 1 payment / 99051 cents' ($declined.Count -eq 1 -and $declined.Amount -eq 99051)
if ($refunded) { Assert-That 'refunds rose by 1 / 3000 cents (the duplicate was not counted)' ($refunds.Count -eq 1 -and $refunds.Amount -eq 3000) }

Write-Host ""
Write-Host ("=" * 78) -ForegroundColor DarkGray
$colour = 'Green'; if ($script:Fail -gt 0) { $colour = 'Red' }
Write-Host ("RESULT: {0} passed, {1} failed" -f $script:Pass, $script:Fail) -ForegroundColor $colour
Write-Host "Follow along in the logs with:  docker compose logs -f worker api" -ForegroundColor DarkGray
if ($script:Fail -gt 0) { exit 1 }
