# System diagrams

Flow diagrams of how the service works, for reviewers and new readers. They are a map, not a spec: `docs/spec.md` is the requirement, `SOLUTION.md` is the design and decisions, and the code is the truth.

These diagrams were checked against the code in three read-only review passes by the AI assistant. No person has reviewed them, the system was not run end to end against them, and rendering on GitHub has not been checked. Section 13 is a proposal from `SOLUTION.md` section 8, not a description of built code, so the code audit does not apply to it.

| # | Diagram | Question it answers |
|---|---|---|
| 1 | [System map](#1-system-map) | What runs, and who talks to whom? |
| 2 | [Accepting a payment](#2-accepting-a-payment) | What does `POST /payments` do, and what can it return? |
| 3 | [Worker submit loop](#3-worker-submit-loop) | How does a payment reach Acme exactly once at a time? |
| 4 | [Acme gate](#4-acme-gate-rate-limit-and-circuit-breaker) | How are the 20 per second limit, 401, 429 and outages handled? |
| 5 | [Classifying Acme's POST answer](#5-classifying-acmes-post-answer) | Approved, declined, failed or unknown? |
| 6 | [Status recompute](#6-status-recompute) | How does a payment's status follow from its events? |
| 7 | [Recovery and RTTQ](#7-recovery-and-rttq) | What happens when the outcome is not known? |
| 8 | [Callbacks](#8-callbacks-advice-and-webhook) | How are Payment Advice and webhooks accepted and applied? |
| 9 | [Status lifecycle](#9-status-lifecycle) | Which status moves are possible? |
| 10 | [Crash recovery sequence](#10-crash-recovery-sequence) | What if a worker dies after sending? |
| 11 | [Data model](#11-data-model) | What is stored? |
| 12 | [Daily summary](#12-daily-summary) | How is the settlement figure computed? |
| 13 | [AWS production design](#13-aws-production-design-proposed) | How would this run on AWS? (proposed, not built) |

## 1. System map

Files: `docker-compose.yml`, `src/Payments.Api/Program.cs`, `src/Payments.Worker/Processing/*`.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart LR
    APP(["Zapper app or curl"])
    PROD(["Real Acme Pay, production only"])

    subgraph COMPOSE ["docker compose"]
        API["Api :5001<br/>POST /payments, GET /payments/id, GET /summary<br/>POST /acme/advice, POST /acme/webhook<br/>runs migrations and demo seed at startup<br/>given no Acme URL and no Redis"]
        WRK["Worker, Concurrency 4 by default<br/>4 submit loops and 4 reconcile loops<br/>reconcile loop 0 also runs 3 sweeps<br/>polls every 500 ms when idle<br/>starts after Api healthy, stub started, Redis healthy<br/>the only caller of Acme"]
        PG[("Postgres 16<br/>source of truth<br/>the only link between Api and Worker")]
        RD[("Redis 7<br/>acme:rate sliding window, 15 per second by default<br/>acme:cooldown after a 429")]
        STUB["Acme stub :5002<br/>magic amounts 99005 99051 99035 99409 99422 99429 99502 99503<br/>callbacks only through /admin/callbacks"]
    end

    APP -->|"HTTP"| API
    API <-->|"read, insert, apply callbacks"| PG
    PG <-->|"claim, record, recompute"| WRK
    WRK <-->|"one token per Acme call, POST and RTTQ share the budget"| RD
    WRK -->|"POST payment and RTTQ query, Acme__BaseUrl is http#58;//stub:8080/"| STUB
    STUB -->|"advice and webhook, only when an operator calls /admin/callbacks"| API
    WRK -. "Acme__BaseUrl points here in production instead of the stub" .-> PROD
    PROD -. "advice with Basic auth, webhook with HMAC" .-> API
```

## 2. Accepting a payment

Files: `src/Payments.Api/Endpoints/PaymentEndpoints.cs`, `src/Payments.Application/Payments/PaymentService.cs`, `PostgresPaymentStore.InsertAsync`.

The Api never calls Acme. It validates, writes one `Pending` row and answers. The Worker does the rest.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    A(["POST /payments"]) --> CT{{"JSON content type?"}}
    CT -->|"no"| X0["415, empty body, from the framework"]
    CT -->|"yes"| B{{"Body binds to SubmitPaymentRequest?"}}
    B -->|"no, malformed JSON or a field of the wrong type"| X1["400 from the framework, empty body in the container"]
    B -->|"yes"| V{{"Validation: paymentId and customerId required, at most 50 characters. subMerchantId required, at most 15. amount above 0"}}
    V -->|"no"| X2["400 problem details, one entry per bad field"]
    V -->|"yes"| SM{{"Sub-merchant exists?"}}
    SM -->|"no"| X3["404 Unknown sub-merchant"]
    SM -->|"yes"| CD{{"Customer has a default stored card?"}}
    CD -->|"no"| X4["404 No default card for this customer"]
    CD -->|"yes"| ID["requestHash = SHA-256 of customerId, subMerchantId and amount<br/>order_number taken from order_number_seq before the insert<br/>transaction_id = new GUID, made once, never changed"]
    ID --> INS["INSERT INTO payments with status Pending<br/>ON CONFLICT app_payment_id DO NOTHING"]
    INS --> N{{"One row inserted?"}}
    N -->|"yes"| R202["202 Accepted, Location header, payment in the body"]
    N -->|"no"| H{{"Existing row has the same requestHash?"}}
    H -->|"yes"| R200["200 with the existing payment, nothing changes"]
    H -->|"no"| R409["409 Payment id already used"]
    R202 --> NT["No Acme call here. The row is Pending and already due, so a Worker claims it next"]
```

A malformed body is answered by the framework itself. `BadRequestExceptionHandler` only adds a problem-details body when the framework throws `BadHttpRequestException` instead of answering directly, so the body is empty in the container (checked against the running stack). A POST with `-d` quoting broken by the shell lands here.

## 3. Worker submit loop

Files: `src/Payments.Worker/Processing/PaymentSubmissionWorker.cs`, `src/Payments.Application/Payments/PaymentSubmissionService.cs`, claim, release and extend SQL in `PostgresPaymentStore.cs`.

The lease is the Acme timeout (40 s) plus `LeaseMargin` (30 s), so 70 s by default.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    L(["Submit loop, waits 500 ms when it did nothing"]) --> P{{"Gate paused? Breaker open, 401 pause, 429 cooldown, Redis down, or the probe is out"}}
    P -->|"yes"| E1(["Back to the top"])
    P -->|"no"| CL["Step 1: CLAIM, one SQL statement<br/>oldest Pending whose next_attempt_at is due<br/>FOR UPDATE SKIP LOCKED<br/>set Submitted, attempt_count + 1, first_sent_at kept or set, lease_until = now + 70 s"]
    CL --> Q{{"Row claimed?"}}
    Q -->|"no"| E2(["Back to the top"])
    Q -->|"yes"| TK["Step 2: TOKEN from the gate, diagram 4<br/>waits up to 3 s"]
    TK --> G{{"Granted?"}}
    G -->|"no"| RL["RELEASE: status Pending, attempt_count - 1, first_sent_at cleared if this was attempt 1, deferred_at = now, next_attempt_at = now + the delay<br/>the attempt is not spent and the payment never expires as stale"]
    RL --> E4(["Back to the top"])
    G -->|"yes"| EX{{"Step 3: EXTEND the lease: still Submitted, same attempt, lease_until still in the future?"}}
    EX -->|"no"| NS["Do NOT send, log a warning<br/>if the lease ran out, lease recovery resolves it through RTTQ"]
    NS --> E3(["Back to the top"])
    EX -->|"yes"| BR["Build the request: TransactionID kept, new MessageID, OrderNumber, amount, card token, sub-merchant, MCC, card acceptor name"]
    BR --> PO["Step 4: POST to Acme<br/>no database transaction open, timeout 40 s<br/>finishes and is recorded even if the host is stopping"]
    PO --> OB["Step 5: OBSERVE: feed the answer to the gate"]
    OB --> RI["Step 6: RECORD in acme_interactions: redacted request, response, status, duration"]
    RI --> SG{{"Gate signal?"}}
    SG -->|"401"| S1["Breaker pauses 60 s, ALERT log"]
    SG -->|"429"| S2["Pause for Retry-After clamped to 1 s to 5 min, default 5 s<br/>publish the cooldown in Redis"]
    S1 --> RL
    S2 --> RL
    SG -->|"none"| CLS["Step 7: CLASSIFY, diagram 5"]
    CLS --> EV["Step 8: APPLY through the shared outcome path: lock the row, insert the event with key post:MessageID, recompute the status, commit"]
    EV --> E5(["Back to the top"])
    classDef loop fill:#f4f4f4,stroke:#999,stroke-dasharray:4 3
    class E1,E2,E3,E4,E5 loop
```

If the gate or the request build throws, the claim is released with no delay, the error is rethrown, and the loop logs it and waits (on shutdown the loop simply exits). If the worker dies after the POST, the lease expires and diagram 7 takes over.

## 4. Acme gate: rate limit and circuit breaker

Files: `src/Payments.Application/Payments/AcmeGate.cs`, `src/Payments.Infrastructure/Acme/RedisAcmeRateLimiter.cs`.

One gate serves both POST and RTTQ calls. The Redis budget is shared by every worker. The breaker state is per process.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    S(["AcquireAsync"]) --> A{{"Pause active?"}}
    A -->|"yes"| D1["Denied, retry after the remaining pause"]
    A -->|"no"| B{{"Breaker needs a probe?"}}
    B -->|"probe already out"| D2["Denied, recheck in 1 s"]
    B -->|"yes, nobody probing"| PR["This call becomes the probe"]
    B -->|"no"| W
    PR --> W
    W["Run the Redis script, atomically"] --> C{{"acme:cooldown key active?"}}
    C -->|"yes"| CD["Pause this worker for the remaining time"]
    CD --> D3["Denied"]
    C -->|"no"| T["Read Redis TIME, drop tokens older than 1 s"]
    T --> F{{"Tokens in the window below the limit? Default 15, allowed 1 to 19"}}
    F -->|"yes"| GR["Granted, token recorded in the window"]
    F -->|"no"| WT{{"Oldest token expires inside the 3 s wait budget?"}}
    WT -->|"yes"| SL["Sleep, check pause again, run the script again"]
    SL --> W
    WT -->|"no"| D4["Denied, retry after that wait"]
    W -. "Redis error or timeout, 1 s limits" .-> UN["Unavailable: pause 2 s, no Acme call, fail closed"]
    UN --> D5["Denied"]
```

A denied or cancelled probe frees the probe slot, so the next caller may probe.

```mermaid
stateDiagram-v2
    [*] --> Closed
    Closed --> Closed: an answer below 500 other than 401 or 429 resets the failure count
    Closed --> Open: 5 failures in a row, 5xx or timeout, pause 30 s
    Closed --> Open: 401, pause 60 s, ALERT
    Closed --> Paused: 429, pause Retry-After clamped 1 s to 5 min, cooldown published to Redis
    Paused --> Closed: pause ends
    Open --> HalfOpen: pause ends, exactly one probe call is let through
    HalfOpen --> Closed: probe answered, not 5xx, not timeout, not 401, a 429 also pauses
    HalfOpen --> Open: probe is 5xx, timeout or 401, pause again at once
    note right of Open
        Per worker process, not shared.
        Only the 429 cooldown is shared through Redis.
        While paused nothing is claimed, so no attempt is spent.
        A probe with no answer is given up after 2 minutes.
    end note
```

## 5. Classifying Acme's POST answer

File: `src/Payments.Application/Payments/AcmeOutcomeClassifier.cs`.

The outcome is decided by the response code: `Success` with a code other than `00` is a decline. `TransactionStatus` only confirms an approval: code `00` with any status other than `Success` is not approved, it is `OutcomeUnknown`.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    R(["Acme POST result"]) --> K{{"Got an HTTP response?"}}
    K -->|"no, timeout or connection error"| U1["OutcomeUnknown"]
    K -->|"yes"| H{{"HTTP status"}}
    H -->|"401 or 429"| GATE["Never classified. The gate released the claim and the payment is back to Pending"]
    H -->|"400"| F1["Failed and ALERT, malformed request, our defect"]
    H -->|"409"| E{{"ResponseCode is E09?"}}
    E -->|"no"| U7["OutcomeUnknown"]
    E -->|"yes"| TX{{"Response TransactionID parses and is ours?"}}
    TX -->|"it is ours"| U2["OutcomeUnknown, Acme already has it, RTTQ resolves it"]
    TX -->|"missing or unparseable"| U8["OutcomeUnknown"]
    TX -->|"another one"| F2["Failed and ALERT, the RRN belongs to another transaction"]
    H -->|"5xx or any other status"| U3["OutcomeUnknown, Acme may have processed it"]
    H -->|"200"| C{{"ResponseCode"}}
    C -->|"empty"| U4["OutcomeUnknown"]
    C -->|"PEND, or COMPL from a source other than EFT"| U5["OutcomeUnknown, not final"]
    C -->|"00, or COMPL from EFT"| S{{"TransactionStatus is Success?"}}
    S -->|"yes"| AP["Approved, auth amount stored"]
    S -->|"no"| U6["OutcomeUnknown"]
    C -->|"any other code"| SRC{{"Source is Acquirer, or the code does not start with E?"}}
    SRC -->|"yes"| DE["Declined"]
    SRC -->|"no, a gateway E code"| F3["Failed, never a decline"]
```

## 6. Status recompute

Files: `src/Payments.Domain/PaymentStatusRules.cs`, `PaymentOutcomeService.Decide`, `PostgresPaymentStore.ApplyEventAsync`.

The status follows from the stored events, so duplicate and out-of-order deliveries converge on the same answer. The one exception is the current status: a `Submitted` payment moves to `Unknown` only when the new event is `OutcomeUnknown`. A flag (`needs_investigation`) is only ever set, never cleared.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    PRE["Before recompute, under the row lock:<br/>auth_amount = the stored value, else the amount on this event if it is Approved, ReversalObserved or RefundObserved<br/>an amount above the requested amount is dropped, the payment is flagged and reconciliation is Exhausted"] --> IN
    IN["Facts: every event stored for the payment, including the new one"] --> O{{"Outcome events present? Approved beats Declined beats Failed"}}
    O -->|"none"| N1{{"Reversal event, or RTTQ saw a reversal?"}}
    N1 -->|"yes"| RV1["Reversed<br/>flag if a refund also exists or the webhook is missing"]
    N1 -->|"no"| N2{{"RTTQ saw a refund?"}}
    N2 -->|"yes"| RF1["Refunded and flagged"]
    N2 -->|"no"| N3{{"Status is Submitted and the new event is OutcomeUnknown?"}}
    N3 -->|"yes"| UK["Unknown"]
    N3 -->|"no"| KEEP["Status unchanged"]
    O -->|"Declined or Failed, no Approved"| ND["Declined if present, else Failed<br/>flag if more than one outcome kind exists, or a reversal or refund exists"]
    O -->|"Approved"| RV{{"Reversal present?"}}
    RV -->|"yes"| RV2["Reversed<br/>flag if outcomes conflict, a refund exists, or the webhook is missing"]
    RV -->|"no"| AU{{"auth_amount known?"}}
    AU -->|"no"| AU2["Approved, flagged if outcomes conflict<br/>Refunded and flagged if RTTQ saw a refund<br/>refund events wait for the amount"]
    AU -->|"yes"| SUM["Add up Refund events in occurred_at order<br/>skip one that is zero or would push the total above auth_amount, and flag it"]
    SUM --> Z{{"Total refunded"}}
    Z -->|"0"| A1["Approved"]
    Z -->|"below auth_amount"| A2["PartiallyRefunded"]
    Z -->|"equals auth_amount"| A3["Refunded"]
    A1 --> OBS{{"RTTQ saw a refund but none was summed?"}}
    OBS -->|"yes"| A4["Refunded and flagged"]
```

On the Approved path the flag is also set when outcomes conflict, when an over-refund was skipped, or when RTTQ saw a refund or reversal that no webhook has confirmed yet.

## 7. Recovery and RTTQ

Files: `src/Payments.Application/Payments/ReconciliationService.cs`, `RttqClassifier.cs`, `src/Payments.Worker/Processing/ReconciliationWorker.cs`.

RTTQ is the transaction status query. A payment never gets a new `TransactionID`, and silence never makes it `Failed` once it may have been sent.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    subgraph SW ["Sweeps, reconcile loop 0 only, on every pass"]
        S1["Submitted and lease_until in the past: the worker died or hung"] -->|"apply OutcomeUnknown, only if still Submitted under the row lock"| UNK["Unknown"]
        S2["Pending, first_sent_at empty, deferred_at empty, created over 15 minutes ago. Skipped while the gate is paused"] -->|"apply Failed, only if still Pending under the row lock"| FL["Failed, a later approval still wins and is flagged"]
        S3["Unmatched webhooks"] --> S3N["see diagram 8"]
    end
    UNK --> C
    AMT["Approved, PartiallyRefunded or Refunded with auth_amount empty"] --> C
    C["CLAIM for RTTQ: reconciliation_state not Exhausted, next_attempt_at due, FOR UPDATE SKIP LOCKED<br/>rttq_attempts + 1, state InProgress, next_attempt_at = now + the lease (Acme timeout + LeaseMargin, 70 s by default)"] --> T{{"Gate token, then extend check: InProgress, same attempt, next_attempt_at still in the future?"}}
    T -->|"gate denied"| DEN["Release: rttq_attempts - 1, retry after the gate's delay"]
    T -->|"extend failed"| SKP["Skip, no call, log a warning"]
    T -->|"ok"| RQ["RTTQ query by TransactionID, recorded in acme_interactions"]
    RQ --> R{{"Result"}}
    R -->|"401 or 429"| REL["Release: rttq_attempts - 1, retry after the pause, no penalty"]
    R -->|"final answer"| EV["Evidence event: Approved, Declined or Failed<br/>from a 200 with Success or Failure and a final code that fits the status, or a 200 Complete with code 00"]
    R -->|"reversed or refunded"| OB["ReversalObserved or RefundObserved, flagged until the webhook arrives<br/>from a 200 with status Reversed or Refunded"]
    R -->|"no usable answer"| BO["Back off<br/>a 200 with Processing, any other status, a non-final code, or Failure with code 00<br/>or a timeout, a 5xx, or any other HTTP status"]
    R -->|"404 not found"| NF{{"Status Unknown, first sent within 10 minutes, rttq_attempts below 8?"}}
    NF -->|"yes"| RS["Resend: status Pending, reconciliation None, SAME TransactionID, new MessageID<br/>the first resend is immediate, later ones back off"]
    NF -->|"no"| GU["Back off with give-up: an Unknown payment is Exhausted at once, an approved payment with no amount keeps backing off"]
    RS --> SUB["The submit loop picks it up, diagram 3"]
    EV --> ST
    OB --> ST
    BO --> ST
    GU --> ST
    ST{{"SETTLE under the row lock"}} -->|"outcome known, and amount known if approved"| DN["reconciliation None, done"]
    ST -->|"authorised amount above the requested amount"| EXH["Exhausted and flagged, auth_amount not stored"]
    ST -->|"unresolved and attempts below 8"| NX["InProgress, next try after 10 s doubling per attempt, capped at 300 s"]
    ST -->|"unresolved, and attempts used up or giving up"| EX2["Exhausted, flagged, ALERT log"]
```

An Exhausted payment keeps its status, is flagged for a person, and is no longer claimed.

## 8. Callbacks: advice and webhook

Files: `src/Payments.Api/Endpoints/CallbackEndpoints.cs`, `src/Payments.Application/Payments/CallbackService.cs`, `ReconciliationService.AdoptUnmatchedWebhooksAsync`.

The advice is matched to a payment by `TransactionID`. The webhook is matched by `order_number`, which is the webhook's `rrn`. Only webhooks go through the late sweep: an unmatched advice is recovered by RTTQ.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    AD(["POST /acme/advice"]) --> A1{{"Basic auth matches? User and password each compared in constant time"}}
    A1 -->|"no"| X401["401 with WWW-Authenticate, nothing stored, warning log"]
    A1 -->|"yes"| RB1{{"Body at most 64 KB?"}}
    RB1 -->|"no"| X413A["413, nothing stored"]
    RB1 -->|"yes"| PARSE
    WH(["POST /acme/webhook"]) --> RB2{{"Body at most 64 KB?"}}
    RB2 -->|"no"| X413B["413, nothing stored"]
    RB2 -->|"yes"| SIGN{{"x-signature equals base64 HMAC-SHA256 of the raw bytes? Constant-time compare before any parsing"}}
    SIGN -->|"no"| X401B["401, nothing stored, warning log"]
    SIGN -->|"yes"| PARSE{{"A JSON object with every required field of the right type? Unknown fields are ignored"}}
    PARSE -->|"no"| REJ[("rejected_callbacks, unique on source and payload hash")]
    REJ --> X400["400, so Acme may resend. RTTQ is the safety net"]
    PARSE -->|"yes"| MER{{"Advice only: MerchantID is ours? The webhook skips this check"}}
    MER -->|"no"| REJ2[("rejected_callbacks")]
    REJ2 --> X403["403"]
    MER -->|"yes"| CLASS["Classify<br/>advice: Success with an approved code approves, other acquirer codes decline, E codes from a non-acquirer source fail, anything else is Informational<br/>webhook: only isApproved with code 00 counts, as Reversal or Refund, everything else is Informational"]
    CLASS --> FIND["Find the payment<br/>advice: transaction_id = TransactionID<br/>webhook: order_number = rrn"]
    FIND --> M{{"Found?"}}
    M -->|"no"| UNM[("payment_events row with payment_id NULL")]
    M -->|"yes"| VAL{{"Webhook Reversal or Refund?"}}
    VAL -->|"yes"| CHK{{"Currency matches, and amount within auth_amount or the requested amount if none?"}}
    CHK -->|"no"| DWN["ALERT, store as Informational, flag the payment"]
    CHK -->|"yes"| APPLY
    VAL -->|"no"| APPLY
    DWN --> APPLY
    subgraph TX ["One transaction"]
        APPLY["Lock the payment row FOR UPDATE"] --> INS["INSERT payment_events ON CONFLICT source and dedup_key DO NOTHING"]
        INS --> NEW{{"Inserted?"}}
        NEW -->|"yes"| RC["Load all events, recompute, write status and flags"]
        NEW -->|"no"| SAME{{"Same payload hash?"}}
        SAME -->|"yes"| DUP["Duplicate, no change"]
        SAME -->|"no"| CF[("event_conflicts row, payment flagged, ALERT log")]
    end
    RC --> DONE["200"]
    DUP --> DONE
    CF --> DONE
    UNM --> DONE
    UNM -. "webhooks only" .-> LATE
    subgraph LATESW ["Late-webhook sweep in the Worker, one pass at a time under a Postgres advisory lock"]
        LATE["Due unmatched webhooks, retried after 10 s doubling, capped at 300 s"] --> F2{{"order_number = rrn matches a payment now?"}}
        F2 -->|"no"| F3["attempts + 1"]
        F3 --> F4{{"12 attempts, or 60 minutes since received?"}}
        F4 -->|"yes"| F5["Exhausted, ALERT log, stop retrying"]
        F2 -->|"yes"| F6["Reversal or Refund: same currency and amount check as above"]
        F6 --> F7["Adopt: set payment_id and event type under the row lock, only while payment_id is still NULL, then recompute"]
    end
```

Dedup keys: advice is `TransactionID|RequestType|TransactionStatus|ResponseCode`, webhook is `transactionUUID`.

| Situation | Answer |
|---|---|
| Bad Basic credentials or bad signature | 401 |
| Body over 64 KB | 413 |
| Missing or invalid field, or non-JSON | 400, body kept in `rejected_callbacks` |
| Advice for another merchant | 403, body kept in `rejected_callbacks` |
| Applied, duplicate, conflict or unmatched | 200 |

## 9. Status lifecycle

Files: `PaymentStatusRules.cs`, `AcmeOutcomeClassifier.cs`, `ReconciliationService.cs`. These are the main moves, not every combination the recompute allows.

```mermaid
stateDiagram-v2
    [*] --> Pending: POST payments, 202
    Pending --> Submitted: worker claim, with a lease
    Submitted --> Pending: gate denied, 401 or 429, attempt returned
    Submitted --> Approved: Success with an approved code
    Submitted --> Declined: final acquirer code other than 00
    Submitted --> Failed: gateway E code, 400, or foreign RRN
    Submitted --> Unknown: timeout, 5xx, non-final code, or lease expired
    Unknown --> Approved: RTTQ, or an advice with Success and an approved code
    Unknown --> Declined: RTTQ or advice with a final decline code
    Unknown --> Failed: RTTQ or advice with a gateway E code
    Unknown --> Reversed: RTTQ reports Reversed, flagged
    Unknown --> Refunded: RTTQ reports Refunded, flagged
    Unknown --> Pending: RTTQ 404 inside the 10 minute resend window
    Pending --> Failed: never sent and older than 15 minutes
    Failed --> Approved: late approval wins, flagged
    Declined --> Approved: late approval wins, flagged
    Approved --> PartiallyRefunded: refund webhook below auth_amount
    Approved --> Refunded: refund webhook equal to auth_amount
    PartiallyRefunded --> Refunded: refunds reach auth_amount
    Approved --> Reversed: reversal webhook
    PartiallyRefunded --> Reversed: reversal webhook, flagged
    Refunded --> Reversed: reversal webhook, flagged
    note right of Unknown
        Exhausted after 8 RTTQ attempts, or a 404 outside the resend window:
        the status stays Unknown, the payment is flagged for a person
    end note
```

## 10. Crash recovery sequence

A crash after the POST never leads to a second payment under a new `TransactionID`: a resend reuses the same `TransactionID`, and only after RTTQ says Acme has no record of it.

```mermaid
sequenceDiagram
    autonumber
    participant W1 as Worker A, submit loop
    participant DB as Postgres
    participant AC as Acme
    participant W2 as Worker, reconcile loop
    W1->>DB: claim attempt 1, with a lease
    W1->>DB: extend lease, still mine
    W1->>AC: POST TransactionID T, MessageID M1
    Note over W1: crash here, or the answer is lost
    Note over DB: lease_until passes
    W2->>DB: sweep finds Submitted with an expired lease
    W2->>DB: event OutcomeUnknown, status Unknown
    W2->>DB: claim for RTTQ, rttq_attempts + 1
    W2->>AC: RTTQ by TransactionID T
    alt Acme processed it
        AC-->>W2: found, approved
        W2->>DB: Rttq evidence, status Approved, auth_amount filled
        Note over W2,AC: no second POST was ever sent
    else Acme never saw it
        AC-->>W2: 404
        W2->>DB: inside the window and attempts below 8, status Pending
        W2->>AC: POST TransactionID T again, MessageID M2
        AC-->>W2: answer
        W2->>DB: apply outcome
    end
```

In the 404 branch the resend is sent by a submit loop that claims the `Pending` row. It is drawn on one lifeline to keep the picture short.

## 11. Data model

Files: `src/Payments.Infrastructure/Schema/v1/V1_00` to `V1_06`. Only the columns that matter to the flows are listed.

```mermaid
erDiagram
    sub_merchants ||--o{ payments : "receives"
    stored_cards ||--o{ payments : "pays with token"
    payments |o--o{ payment_events : "facts, status derives from these"
    payments ||--o{ acme_interactions : "every Acme call, redacted"
    payments |o--o{ event_conflicts : "same key, different payload"
    payment_events |o--o| unmatched_event_sweeps : "retry bookkeeping by event_id, no foreign key"
    acme_interactions {
        bigint id PK
        uuid payment_id FK
        smallint kind "POST or RTTQ"
        uuid message_id UK "new for every attempt"
        int response_status
        int duration_ms
    }
    payments {
        uuid id PK
        text app_payment_id UK "idempotency key"
        text request_hash "idempotency compare"
        uuid transaction_id UK "stable across resends"
        text order_number UK "12 characters, matches the webhook rrn"
        bigint amount_minor
        bigint auth_amount "from Acme, first value wins"
        text status
        int attempt_count
        int rttq_attempts
        text reconciliation_state "None, InProgress or Exhausted"
        timestamptz lease_until "submit lease"
        timestamptz next_attempt_at "due time and RTTQ lease"
        timestamptz first_sent_at
        timestamptz deferred_at
        bool needs_investigation
    }
    payment_events {
        bigint id PK
        uuid payment_id FK "NULL while unmatched"
        smallint source "1 post, 2 advice, 3 webhook, 4 rttq, 5 system"
        smallint event_type "1 Approved, 2 Unknown, 3 Declined, 4 Failed, 5 Reversal, 6 Refund, 7 Informational, 8 ReversalObserved, 9 RefundObserved"
        text dedup_key "unique with source"
        text payload_hash
        timestamptz occurred_at "Acme time"
        timestamptz received_at
        bigint amount_minor
    }
    event_conflicts {
        bigint id PK
        uuid payment_id FK "NULL if unmatched"
        smallint source
        text dedup_key
        text payload_hash
    }
    rejected_callbacks {
        smallint source
        text reason
        text payload_hash "unique with source"
    }
    unmatched_event_sweeps {
        bigint event_id PK
        int attempts
        timestamptz last_attempt_at
        timestamptz exhausted_at
    }
```

`rejected_callbacks` has no `payment_id` column, so it is not linked to `payments`.

## 12. Daily summary

Files: `sql/daily-summary.sql`, `src/Payments.Application/Payments/DailySummary.cs`, `src/Payments.Api/Endpoints/SummaryEndpoints.cs`.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    Q(["GET /summary?date=yyyy-MM-dd"]) --> B["Bounds: the SAST day start and end<br/>cutoff = day end + Summary__CutoffHours, 12 by default<br/>settled = now is at or after the cutoff"]
    B --> C["Candidates: payments with an Approved or Declined event whose effective time falls in the day<br/>effective time = occurred_at, else received_at"]
    C --> FO["One outcome per payment, by LATERAL lookup: its earliest Approved, else its earliest Declined<br/>kept only if that event's own effective time is in the day"]
    FO --> F1["approved: amount = auth_amount, else the requested amount<br/>declined: the requested amount"]
    B --> F2["Reversals and refunds: events of type Reversal or Refund (only webhooks produce them) in the day on matched payments<br/>one per transactionUUID, amount taken from the webhook, including ones the status rules flag instead of applying: a refund total above auth_amount, a refund after a reversal<br/>not counted: a webhook whose currency or single amount contradicts the payment, stored as Informational"]
    F1 --> U["facts"]
    F2 --> U
    U --> LT["late = received_at at or after the cutoff"]
    LT --> G["GROUP BY sub_merchant_id and currency<br/>count and amount for approved, declined, reversals and refunds, plus the late part of each"]
    G --> O(["JSON rows with cutoffAt and settled<br/>gross figures: a later-reversed approval still counts as approved on its own day"])
```

The SQL counts every stored `Reversal` and `Refund` event in the day. Two of the rules in the diagram are kept by the code that stores events, not by the query. "One per `transactionUUID`" comes from the unique key on `payment_events` (`source`, `dedup_key`). "Not counted" for a contradicting webhook comes from the callback code storing it as `Informational` instead of as a `Reversal` or `Refund`.

## 13. AWS production design (proposed)

Source: `SOLUTION.md` section 8. **This is a proposal, not built.** What exists today is the application and its failure behaviour (leases, RTTQ recovery, the shared limiter, graceful shutdown, JSON logs, secrets from environment variables). The AWS infrastructure, caller auth on the app API, TLS and metrics do not exist in the repository.

```mermaid
%%{init: {"flowchart": {"nodeSpacing": 30, "rankSpacing": 40, "wrappingWidth": 300}}}%%
flowchart TD
    APP(["Zapper app"])
    ACMEIN(["Acme Pay, sending callbacks"])
    ACMEOUT(["Acme Pay, receiving our calls"])

    R53["Route 53"]
    WAF["AWS WAF"]

    subgraph VPC ["VPC across three AZs, one AWS account per environment"]
        ALB["ALB<br/>public subnets, TLS from ACM<br/>app calls and Acme callbacks"]
        NAT["NAT gateways, public subnets<br/>fixed elastic IPs, so Acme can allow-list us"]
        API["ECS Fargate: Api service, private subnets<br/>at least 2 tasks<br/>scales on CPU and request count"]
        WRK["ECS Fargate: Worker service, private subnets<br/>at least 2 tasks, no inbound traffic<br/>scales on age of the oldest Pending payment"]
        MIG["One-off ECS task: migrations<br/>run by the pipeline before a rollout"]
        PX["RDS Proxy"]
        RDS[("RDS PostgreSQL, Multi-AZ<br/>PITR backups, KMS at rest<br/>source of truth and work queue")]
        REDIS[("ElastiCache Redis<br/>replica and automatic failover<br/>shared rate window and 429 cooldown")]
    end

    subgraph PLAT ["Platform services, outside the request path"]
        PIPE["CI/CD pipeline<br/>build, test, scan, staging, approval, prod<br/>runs the migration task before a rollout"]
        ECR["ECR<br/>same images as Compose, promoted by digest"]
        SEC["Secrets Manager or SSM<br/>Acme API key, advice credentials, webhook secret, DB password<br/>injected into the Api and Worker as environment variables"]
    end

    subgraph OBS ["Observability"]
        CW["CloudWatch Logs<br/>JSON from stdout via the awslogs driver"]
        ALM["CloudWatch metric filters and alarms<br/>on ALERT lines, 429s, oldest Pending age"]
        SNS["SNS"]
        ONCALL(["On-call"])
    end

    APP -->|"HTTPS, caller auth proposed"| R53
    ACMEIN -->|"advice with Basic auth, webhook with HMAC"| R53
    R53 --> WAF
    WAF --> ALB
    ALB --> API
    API --> PX
    WRK --> PX
    MIG --> PX
    PX --> RDS
    WRK -->|"one token per Acme call"| REDIS
    WRK -->|"POST and RTTQ"| NAT
    NAT --> ACMEOUT

    PIPE -.-> ECR
    PLAT -.-> VPC
    VPC -.->|"JSON logs"| CW
    CW --> ALM
    ALM --> SNS
    SNS --> ONCALL
```

Notes on the picture:

- The Api has no Redis access and never calls Acme, as in the local build (diagram 1). Only the Worker reaches Redis and goes out through NAT.
- Adding Workers never exceeds Acme's limit, because the budget lives in Redis (diagram 4).
- RDS Proxy is there for connection management. Each Api and Worker task has its own Npgsql pool, so autoscaling multiplies connections against Postgres' limit (a burst once gave `53300 too many clients already`, see `SOLUTION.md` section 13). The code caps each process's pool at 40, which is enough for a few tasks. The proxy pools connections as tasks grow and keeps them open through a Multi-AZ failover. It is optional at two tasks per service. The worker's advisory lock is transaction-level, so it works through the proxy; a session-level lock would pin the connection.
- VPC endpoints for ECR, S3, Secrets Manager and CloudWatch keep that traffic off the internet. Each service has its own IAM task role, and the Api and Worker use separate database roles. These are left out of the picture for readability.
- Migrations run as the one-off task, not at Api startup. The code still migrates at startup by default (`Database__MigrateOnStartup` is true), so this is a change the AWS design would make.
- Stop timeout: ECS `stopTimeout` must exceed the worker's host shutdown timeout (Acme timeout + 15 s, 55 s by default).

| Failure | What happens | Built? |
|---|---|---|
| Worker killed or scaled in | SIGTERM: stop claiming, finish the in-flight Acme call. A hard kill leaves an expiring lease, which RTTQ resolves with no blind resend | Yes, in the code |
| Postgres failover | Leases expire, RTTQ resolves them. No payment is lost | Yes, in the code |
| Redis down | The first failed limiter call pauses that worker for 2 s at a time, so it claims nothing while paused. A payment claimed just before the failure is released with no attempt spent. Payment state stays in Postgres | Yes, in the code |
| Acme outage | The breaker opens, nothing is claimed, payments wait in `Pending` | Yes, in the code |
| Loss of an AZ | Multi-AZ RDS and tasks in each AZ continue after a failover of a minute or two | Proposed |
| Bad deploy | Rolling or blue/green, rollback on a failed health check or alarm | Proposed |
