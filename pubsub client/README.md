# Einstein Chat — Pub/Sub Architecture

Refactor of the Einstein Chat LWC to replace REST/HTTP calls with **Salesforce Platform Events** over the **Pub/Sub API (gRPC)**.

## Architecture

```
┌──────────────────────────┐                          ┌──────────────────────────┐
│   LWC (einsteinChat)     │                          │   External Subscriber    │
│                          │                          │   (Node.js / gRPC)       │
│  1. sendMessage()        │                          │                          │
│     └─ Apex publish ─────┼──► Einstein_Chat_        │  2. Subscribe to         │
│                          │     Request__e  ─────────┼──►  Request__e           │
│                          │                          │                          │
│                          │                          │  3. Run LLM / agents     │
│                          │                          │                          │
│  5. empApi subscription  ◄┼── Einstein_Chat_        │  4. Publish to           │
│     (filtered by         │    Response__e ◄─────────┼──  Response__e           │
│     Conversation_Id__c)  │                          │                          │
└──────────────────────────┘                          └──────────────────────────┘
```

**What changed vs. the REST version:**

- The LWC no longer calls `fetch()` against `/api/chat` or `/api/agent-chat`.
- A new Apex class `EinsteinChatPublisher` publishes `Einstein_Chat_Request__e` events.
- The LWC subscribes to `/event/Einstein_Chat_Response__e` via `lightning/empApi` and dispatches events to the originating turn using a per-turn `Conversation_Id__c`.
- The external service is now a gRPC subscriber on Salesforce's Pub/Sub API instead of an HTTP server.
- A 45-second client-side timeout protects against lost responses.

## Repository layout

```
force-app/main/default/
├── lwc/einsteinChat/              Refactored LWC (JS + HTML + CSS + meta)
├── classes/                       Apex publisher + test
│   ├── EinsteinChatPublisher.cls
│   └── EinsteinChatPublisherTest.cls
└── objects/
    ├── Einstein_Chat_Request__e/   Request platform event + 5 fields
    └── Einstein_Chat_Response__e/  Response platform event + 4 fields

external-subscriber/
├── subscriber.js                   gRPC subscriber entry point
├── package.json
├── .env.example
└── PROTO_FILE.md                   How to obtain pubsub_api.proto
```

## Platform event schema

### `Einstein_Chat_Request__e` (published by LWC, consumed by subscriber)

| Field | Type | Purpose |
|---|---|---|
| `Conversation_Id__c` | Text(64) | Correlation ID. LWC filters its response subscription on this. |
| `Mode__c` | Text(16) | `"chat"` or `"agents"`. |
| `User_Message__c` | LongText(32K) | The current turn's user message. |
| `History_Json__c` | LongText(128K) | Prior turns as JSON `[{role, content}...]`. |
| `User_Id__c` | Text(18) | Stamped server-side from `UserInfo.getUserId()`. |

### `Einstein_Chat_Response__e` (published by subscriber, consumed by LWC)

| Field | Type | Purpose |
|---|---|---|
| `Conversation_Id__c` | Text(64) | Must match the request's correlation ID. |
| `Status__c` | Text(16) | `"ok"` or `"error"`. |
| `Payload_Json__c` | LongText(128K) | See payload shapes below. |
| `Error_Message__c` | Text(1024) | Populated when `Status__c = "error"`. |

### Payload JSON shapes

**Chat mode** (`Mode__c = "chat"`):
```json
{
  "replyText": "…",
  "updatedHistory": [{ "role": "user", "content": "…" }, …]
}
```

**Agents mode** (`Mode__c = "agents"`):
```json
{
  "responses": [
    { "agentName": "AccountsAgent",      "answer": "…" },
    { "agentName": "OpportunitiesAgent", "answer": "…" }
  ],
  "suggestions": ["Follow-up 1", "Follow-up 2"],
  "updatedHistory": [{ "role": "user", "content": "…" }, …]
}
```

## Deploy — Salesforce side

```bash
sf project deploy start --source-dir force-app
sf apex run test --class-names EinsteinChatPublisherTest --result-format human
```

Then add the `einsteinChat` component to any Lightning page via the App Builder.

## Run — External subscriber

1. Create a **Connected App** in Salesforce with the `api` and `refresh_token` OAuth scopes.
2. Download the official proto file into `external-subscriber/` (see `PROTO_FILE.md`).
3. Install and run:

   ```bash
   cd external-subscriber
   cp .env.example .env      # fill in credentials
   npm install
   npm start
   ```

4. Replace the stub `handleChat()` and `routeToAgents()` functions in `subscriber.js` with real LLM / agent calls.

## Required permissions

The integration user (and any end user of the LWC) needs:

- `Create` on `Einstein_Chat_Request__e` (for the LWC user — the Apex publisher runs as the caller).
- `Subscribe` on `Einstein_Chat_Response__e` (for the LWC user).
- `Subscribe` on `Einstein_Chat_Request__e` **and** `Create` on `Einstein_Chat_Response__e` (for the external subscriber's integration user).

## Notes & trade-offs

- **Timeout**: The LWC waits up to 45s for a response event. Tune `RESPONSE_TIMEOUT_MS` in `einsteinChat.js` if the LLM can be slower.
- **No streaming**: This design sends one aggregated response per turn (per your spec). Switching to per-agent streaming is additive — publish multiple response events with the same `Conversation_Id__c` and change the LWC handler to append instead of replace.
- **Replay**: The LWC subscribes with replay ID `-1` (new events only) so reloads don't replay stale responses. The external subscriber uses `LATEST` for the same reason — in production you likely want to persist the last processed replayId to survive restarts without dropping events.
- **Fan-out**: Because the LWC filters on `Conversation_Id__c`, multiple users / tabs / components can share the same response channel without crosstalk.
