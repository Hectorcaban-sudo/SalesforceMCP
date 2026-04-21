/**
 * Einstein Chat — External Pub/Sub API Subscriber
 *
 * Replaces the previous REST endpoints (/api/chat, /api/agent-chat) with a
 * gRPC subscription to Salesforce's Pub/Sub API.
 *
 * Flow:
 *   1. Subscribe to /event/Einstein_Chat_Request__e
 *   2. For each incoming event:
 *        - decode Avro payload
 *        - route to chat handler or agent orchestrator based on Mode__c
 *        - build aggregated response payload
 *   3. Publish /event/Einstein_Chat_Response__e with the same Conversation_Id__c
 *
 * Auth: OAuth 2.0 username-password flow (swap for JWT/client-credentials in prod).
 */

const fs            = require('fs');
const path          = require('path');
const grpc          = require('@grpc/grpc-js');
const protoLoader   = require('@grpc/proto-loader');
const avro          = require('avro-js');
const axios         = require('axios');

// ── Config ────────────────────────────────────────────────────────────────────
const {
    SF_LOGIN_URL         = 'https://login.salesforce.com',
    SF_CLIENT_ID,
    SF_CLIENT_SECRET,
    SF_USERNAME,
    SF_PASSWORD,          // password + security token concatenated
    PUBSUB_ENDPOINT       = 'api.pubsub.salesforce.com:7443',
    REQUEST_TOPIC         = '/event/Einstein_Chat_Request__e',
    RESPONSE_TOPIC        = '/event/Einstein_Chat_Response__e',
    BATCH_SIZE            = '10',
} = process.env;

const PROTO_PATH = path.join(__dirname, 'pubsub_api.proto');

// ── OAuth ─────────────────────────────────────────────────────────────────────
async function authenticate() {
    const res = await axios.post(
        `${SF_LOGIN_URL}/services/oauth2/token`,
        new URLSearchParams({
            grant_type:    'password',
            client_id:     SF_CLIENT_ID,
            client_secret: SF_CLIENT_SECRET,
            username:      SF_USERNAME,
            password:      SF_PASSWORD,
        }).toString(),
        { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } }
    );
    // instance_url comes back as https://<domain>; tenantId is the org id.
    // For Pub/Sub API we need the org id — fetch it from the identity URL.
    const identity = await axios.get(res.data.id, {
        headers: { Authorization: `Bearer ${res.data.access_token}` },
    });
    return {
        accessToken: res.data.access_token,
        instanceUrl: res.data.instance_url,
        tenantId:    identity.data.organization_id,
    };
}

// ── gRPC client ───────────────────────────────────────────────────────────────
function buildPubSubClient() {
    const packageDefinition = protoLoader.loadSync(PROTO_PATH, {
        keepCase: true,
        longs:    String,
        enums:    String,
        defaults: true,
        oneofs:   true,
    });
    const proto = grpc.loadPackageDefinition(packageDefinition).eventbus.v1;
    return new proto.PubSub(PUBSUB_ENDPOINT, grpc.credentials.createSsl());
}

function authMetadata({ accessToken, instanceUrl, tenantId }) {
    const meta = new grpc.Metadata();
    meta.add('accesstoken', accessToken);
    meta.add('instanceurl', instanceUrl);
    meta.add('tenantid',    tenantId);
    return meta;
}

// ── Schema cache ──────────────────────────────────────────────────────────────
// The Pub/Sub API delivers Avro-encoded events. We cache parsed Avro types
// per schemaId to avoid re-fetching on every message.
const schemaCache = new Map();

async function getSchema(client, meta, schemaId) {
    if (schemaCache.has(schemaId)) return schemaCache.get(schemaId);

    const schemaInfo = await new Promise((resolve, reject) => {
        client.GetSchema({ schema_id: schemaId }, meta, (err, response) =>
            err ? reject(err) : resolve(response)
        );
    });
    const type = avro.parse(schemaInfo.schema_json);
    schemaCache.set(schemaId, type);
    return type;
}

async function getTopicSchema(client, meta, topic) {
    const topicInfo = await new Promise((resolve, reject) => {
        client.GetTopic({ topic_name: topic }, meta, (err, response) =>
            err ? reject(err) : resolve(response)
        );
    });
    return getSchema(client, meta, topicInfo.schema_id).then((type) => ({
        schemaId: topicInfo.schema_id,
        type,
    }));
}

// ── Handlers: plug your LLM / agent logic here ────────────────────────────────

/**
 * Chat-mode handler. Append the user message to history, call the LLM,
 * return { replyText, updatedHistory }.
 *
 * Replace the stub with a real LLM call (OpenAI, Anthropic, Einstein, etc.).
 */
async function handleChat({ userMessage, history }) {
    const newHistory = [...history, { role: 'user', content: userMessage }];

    // TODO: replace with your LLM client
    // const completion = await llm.chat({ messages: newHistory });
    // const replyText  = completion.choices[0].message.content;
    const replyText = `(stub) You said: "${userMessage}". Wire handleChat() to your LLM.`;

    newHistory.push({ role: 'assistant', content: replyText });
    return { replyText, updatedHistory: newHistory };
}

/**
 * Agents-mode handler. Routes the question to Accounts / Opportunities /
 * Contracts specialists, aggregates their answers, and returns the combined
 * payload the LWC expects.
 *
 * Replace routeToAgents() with your real agent orchestrator.
 */
async function handleAgents({ userMessage, history }) {
    const newHistory = [...history, { role: 'user', content: userMessage }];

    // TODO: swap for a real orchestrator (LangGraph, Einstein Agents, etc.).
    // Expected shape: [{ agentName, answer }...]
    const responses = await routeToAgents(userMessage, newHistory);

    // Combine agent answers into a single assistant turn so future LLM calls
    // see a coherent history.
    const joined = responses.map((r) => `[${r.agentName}] ${r.answer}`).join('\n\n');
    newHistory.push({ role: 'assistant', content: joined });

    const suggestions = deriveFollowUps(responses);
    return { responses, suggestions, updatedHistory: newHistory };
}

async function routeToAgents(userMessage /*, history */) {
    // Stub: pretend Accounts always has an opinion.
    return [
        { agentName: 'AccountsAgent', answer: `(stub) Accounts agent received: "${userMessage}"` },
    ];
}

function deriveFollowUps(responses) {
    // Replace with real follow-up generation. Max 6.
    const base = ['Show me more detail', 'Export this to CSV', 'Who owns these records?'];
    return base.slice(0, Math.min(6, base.length));
}

// ── Publish helper ────────────────────────────────────────────────────────────
async function publishResponse(client, meta, { conversationId, status, payloadJson, errorMessage }) {
    const { schemaId, type } = await getTopicSchema(client, meta, RESPONSE_TOPIC);

    const record = {
        CreatedDate:        Date.now(),
        CreatedById:        'AGENT',   // ignored by the platform for published events
        Conversation_Id__c: { string: conversationId },
        Payload_Json__c:    payloadJson    ? { string: payloadJson }    : null,
        Status__c:          { string: status },
        Error_Message__c:   errorMessage   ? { string: errorMessage }   : null,
    };

    const encoded = type.toBuffer(record);

    await new Promise((resolve, reject) => {
        client.Publish(
            {
                topic_name: RESPONSE_TOPIC,
                events: [{ schema_id: schemaId, payload: encoded }],
            },
            meta,
            (err, response) => {
                if (err) return reject(err);
                const result = response.results?.[0];
                if (result?.error) return reject(new Error(result.error.msg));
                resolve(response);
            }
        );
    });
}

// ── Event processing ──────────────────────────────────────────────────────────
async function processEvent(client, meta, decoded) {
    const conversationId = decoded.Conversation_Id__c;
    const mode           = decoded.Mode__c;
    const userMessage    = decoded.User_Message__c;
    const historyJson    = decoded.History_Json__c || '[]';

    let history = [];
    try {
        history = JSON.parse(historyJson);
        if (!Array.isArray(history)) history = [];
    } catch {
        history = [];
    }

    try {
        const result = mode === 'agents'
            ? await handleAgents({ userMessage, history })
            : await handleChat({ userMessage, history });

        await publishResponse(client, meta, {
            conversationId,
            status:      'ok',
            payloadJson: JSON.stringify(result),
        });
        console.log(`[${conversationId}] ok (${mode})`);
    } catch (err) {
        console.error(`[${conversationId}] handler failed`, err);
        try {
            await publishResponse(client, meta, {
                conversationId,
                status:       'error',
                errorMessage: (err.message || 'Handler error').slice(0, 1024),
            });
        } catch (publishErr) {
            console.error(`[${conversationId}] could not publish error response`, publishErr);
        }
    }
}

// ── Subscribe loop ────────────────────────────────────────────────────────────
async function subscribe(client, meta) {
    const { type: requestType } = await getTopicSchema(client, meta, REQUEST_TOPIC);

    const call = client.Subscribe(meta);

    call.on('data', async (event) => {
        // Replenish the flow-control window as we consume events.
        if (event.events && event.events.length > 0) {
            for (const e of event.events) {
                try {
                    const decoded = requestType.fromBuffer(e.event.payload);
                    // Don't await — process in parallel so slow LLM calls don't
                    // block the subscription stream.
                    processEvent(client, meta, decoded).catch((err) =>
                        console.error('processEvent rejected', err)
                    );
                } catch (err) {
                    console.error('Failed to decode event', err);
                }
            }
            call.write({ topic_name: REQUEST_TOPIC, num_requested: event.events.length });
        }
    });

    call.on('error', (err) => {
        console.error('Subscription error', err);
        // In prod: exponential backoff + resubscribe with stored replayId.
        process.exit(1);
    });

    call.on('end', () => {
        console.log('Subscription stream ended');
        process.exit(0);
    });

    // Kick off the initial fetch request.
    call.write({
        topic_name:       REQUEST_TOPIC,
        replay_preset:    'LATEST',
        num_requested:    parseInt(BATCH_SIZE, 10),
    });

    console.log(`Subscribed to ${REQUEST_TOPIC}`);
}

// ── Main ──────────────────────────────────────────────────────────────────────
(async () => {
    const required = ['SF_CLIENT_ID', 'SF_CLIENT_SECRET', 'SF_USERNAME', 'SF_PASSWORD'];
    const missing  = required.filter((k) => !process.env[k]);
    if (missing.length) {
        console.error(`Missing required env vars: ${missing.join(', ')}`);
        process.exit(1);
    }

    const auth   = await authenticate();
    const client = buildPubSubClient();
    const meta   = authMetadata(auth);

    await subscribe(client, meta);
})();
