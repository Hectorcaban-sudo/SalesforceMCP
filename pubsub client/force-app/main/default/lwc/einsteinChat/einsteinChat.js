import { LightningElement, api, track, wire } from 'lwc';
import { subscribe, unsubscribe, onError } from 'lightning/empApi';
import { getRecord, getFieldValue } from 'lightning/uiRecordApi';
import publishRequest from '@salesforce/apex/EinsteinChatPublisher.publishRequest';

import USER_ID from '@salesforce/user/Id';
import NAME_FIELD from '@salesforce/schema/User.Name';
import FIRST_NAME_FIELD from '@salesforce/schema/User.FirstName';
import LAST_NAME_FIELD from '@salesforce/schema/User.LastName';

const USER_FIELDS = [NAME_FIELD, FIRST_NAME_FIELD, LAST_NAME_FIELD];

const RESPONSE_CHANNEL = '/event/Einstein_Chat_Response__e';
const RESPONSE_TIMEOUT_MS = 45000;   // subscriber has 45s to respond before we give up

const MODE_CHAT   = 'chat';
const MODE_AGENTS = 'agents';

const AGENT_META = {
    AccountsAgent:      { label: 'Accounts',     icon: 'utility:account',     color: 'agent-blue'   },
    OpportunitiesAgent: { label: 'Opportunities', icon: 'utility:opportunity', color: 'agent-green'  },
    ContractsAgent:     { label: 'Contracts',     icon: 'utility:contract',    color: 'agent-purple' },
};

// ── Record-aware prompt suggestions ─────────────────────────────────────────
// Per-object prompt templates. The {name} token is replaced at render time
// with the live record's display field value (see DISPLAY_FIELD below).
// The record ID is never shown — only the friendly name.
const RECORD_PROMPTS = {
    Opportunity: [
        'Summarize {name}',
        'What are the next steps to close {name}?',
        'Show recent activity on {name}',
        'What competitors are involved on {name}?',
    ],
    Account: [
        'Summarize {name}',
        'Show open opportunities for {name}',
        'List recent cases for {name}',
        'Who are the key contacts at {name}?',
    ],
    Case: [
        'Summarize {name}',
        'Suggest a resolution for {name}',
        'Show similar past cases',
        'Draft a reply to the customer',
    ],
    Contact: [
        'Summarize {name}',
        'Show recent interactions with {name}',
        'Draft a follow-up email to {name}',
        'What opportunities is {name} on?',
    ],
    Lead: [
        'Summarize {name}',
        'Is {name} worth pursuing?',
        'Draft an outreach email to {name}',
        'Suggest next steps for {name}',
    ],
    Contract: [
        'Summarize {name}',
        'When does {name} expire?',
        'What are the key terms of {name}?',
        'Show related opportunities',
    ],
};

// Configurable display field per object — what to show as the record's
// "name" in prompt chips. Defaults to "Name" for any object not listed.
// Override here per org/customization need.
const DISPLAY_FIELD = {
    Opportunity: 'Name',
    Account:     'Name',
    Case:        'CaseNumber',     // numeric case identifier reads better than Subject
    Contact:     'Name',
    Lead:        'Name',
    Contract:    'ContractNumber',
    User:        'Name',
};

// Friendly object label for the welcome copy (fallback to the API name).
const OBJECT_LABELS = {
    Opportunity: 'opportunity',
    Account: 'account',
    Case: 'case',
    Contact: 'contact',
    Lead: 'lead',
    Contract: 'contract',
};

let msgIdCounter = 0;
const uid = () => `msg-${++msgIdCounter}`;

// Conversation IDs correlate request→response and scope the empApi filter.
// Using crypto.randomUUID where available, falling back to a timestamped id.
const newConversationId = () => {
    if (typeof crypto !== 'undefined' && crypto.randomUUID) {
        return crypto.randomUUID();
    }
    return `conv-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
};

const formatTime = (date) =>
    date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

// Build up-to-two-letter initials from first/last name, falling back to the
// full name, then to a generic "ME".
const deriveInitials = (first, last, fullName) => {
    if (first || last) {
        return `${(first || '').charAt(0)}${(last || '').charAt(0)}`.toUpperCase() || 'ME';
    }
    if (fullName) {
        const parts = fullName.trim().split(/\s+/);
        const a = parts[0]?.charAt(0) || '';
        const b = parts.length > 1 ? parts[parts.length - 1].charAt(0) : '';
        return `${a}${b}`.toUpperCase() || 'ME';
    }
    return 'ME';
};

export default class EinsteinChat extends LightningElement {

    @track uiMessages   = [];
    @track inputValue   = '';
    @track isLoading    = false;
    @track errorMessage = null;
    @track isMinimized  = true;
    @track isMaximized  = false;
    @track unreadCount  = 0;
    @track mode         = MODE_CHAT;

    // ── Record context ──────────────────────────────────────────────────────
    // When the component is placed on a record page, the Lightning runtime
    // auto-populates these. On home/app pages they're undefined and the
    // component falls back to generic prompts.
    @api recordId;
    @api objectApiName;

    history = [];

    // ── Current user ──────────────────────────────────────────────────────────
    // Pulls the logged-in user's name so message bubbles show the real user
    // instead of a hardcoded "John Doe".
    userId = USER_ID;

    @wire(getRecord, { recordId: '$userId', fields: USER_FIELDS })
    userRecord;

    get currentUserName() {
        return this.userRecord?.data
            ? getFieldValue(this.userRecord.data, NAME_FIELD)
            : 'You';
    }

    get currentUserInitials() {
        if (!this.userRecord?.data) return 'ME';
        return deriveInitials(
            getFieldValue(this.userRecord.data, FIRST_NAME_FIELD),
            getFieldValue(this.userRecord.data, LAST_NAME_FIELD),
            getFieldValue(this.userRecord.data, NAME_FIELD)
        );
    }

    // ── Current record ────────────────────────────────────────────────────────
    // When on a record page, wire the record to fetch its display field so we
    // can show the record's *name* (not its ID) in the prompt chips. The record
    // ID is still sent on the published event — it's just never rendered to
    // the user.
    //
    // The fields list is dynamic (`'Opportunity.Name'`-style strings) so we
    // don't need a compile-time @salesforce/schema import per object.
    get _recordFields() {
        if (!this.objectApiName) return [];
        const field = DISPLAY_FIELD[this.objectApiName] || 'Name';
        return [`${this.objectApiName}.${field}`];
    }

    @wire(getRecord, { recordId: '$recordId', fields: '$_recordFields' })
    currentRecord;

    // Returns the display name of the current record, or null when unavailable
    // (wire still loading, no record context, or field not readable).
    get recordDisplayName() {
        if (!this.currentRecord?.data || !this.objectApiName) return null;
        const field = DISPLAY_FIELD[this.objectApiName] || 'Name';
        const value = this.currentRecord.data.fields?.[field]?.value;
        return value != null && value !== '' ? String(value) : null;
    }

    // ── Pub/Sub state ─────────────────────────────────────────────────────────
    // Single long-lived subscription to the response channel. Each outgoing
    // request tags itself with a conversationId; incoming events are dispatched
    // to the matching pending request via _pendingByConvId.
    _subscription      = null;
    _pendingByConvId   = new Map();   // conversationId → { resolve, reject, timeoutId }
    _empApiErrorBound  = false;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    async connectedCallback() {
        if (!this._empApiErrorBound) {
            onError((err) => {
                // Surfaced to the console; individual turns time out independently.
                // eslint-disable-next-line no-console
                console.error('[einsteinChat] empApi error', err);
            });
            this._empApiErrorBound = true;
        }
        await this._ensureSubscribed();
    }

    disconnectedCallback() {
        this._teardown();
    }

    async _ensureSubscribed() {
        if (this._subscription) return;
        try {
            // -1 replayId = only new events from here forward (no history replay).
            this._subscription = await subscribe(
                RESPONSE_CHANNEL,
                -1,
                (event) => this._handleResponseEvent(event)
            );
        } catch (err) {
            this.errorMessage = 'Could not connect to Einstein event stream.';
            // eslint-disable-next-line no-console
            console.error('[einsteinChat] subscribe failed', err);
        }
    }

    _teardown() {
        // Reject any in-flight turns so their UI cleans up
        for (const [, pending] of this._pendingByConvId) {
            clearTimeout(pending.timeoutId);
            pending.reject(new Error('Chat component unmounted'));
        }
        this._pendingByConvId.clear();

        if (this._subscription) {
            unsubscribe(this._subscription).catch(() => { /* swallow */ });
            this._subscription = null;
        }
    }

    // ── Response dispatch ─────────────────────────────────────────────────────
    _handleResponseEvent(event) {
        // empApi shape: { data: { schema, payload: {...}, event: { replayId } } }
        const payload = event?.data?.payload;
        if (!payload) return;

        const convId = payload.Conversation_Id__c;
        const pending = this._pendingByConvId.get(convId);
        // Not ours (another tab, stale, or a different user's event) — ignore.
        if (!pending) return;

        clearTimeout(pending.timeoutId);
        this._pendingByConvId.delete(convId);

        if (payload.Status__c === 'error') {
            pending.reject(new Error(payload.Error_Message__c || 'Subscriber reported an error'));
            return;
        }

        let parsed;
        try {
            parsed = JSON.parse(payload.Payload_Json__c || '{}');
        } catch (e) {
            pending.reject(new Error('Malformed response payload'));
            return;
        }
        pending.resolve(parsed);
    }

    // Publishes a request and returns a promise that resolves with the parsed
    // response payload (or rejects on timeout / error).
    _awaitResponse(conversationId, mode, userMessage, historyJson) {
        const responsePromise = new Promise((resolve, reject) => {
            const timeoutId = setTimeout(() => {
                this._pendingByConvId.delete(conversationId);
                reject(new Error('Einstein did not respond in time. Please try again.'));
            }, RESPONSE_TIMEOUT_MS);

            this._pendingByConvId.set(conversationId, { resolve, reject, timeoutId });
        });

        // Fire the publish; if it fails synchronously, clean up the pending entry.
        publishRequest({
            conversationId,
            mode,
            userMessage,
            historyJson,
            recordId:      this.recordId || null,
            objectApiName: this.objectApiName || null,
        })
            .catch((err) => {
                const pending = this._pendingByConvId.get(conversationId);
                if (pending) {
                    clearTimeout(pending.timeoutId);
                    this._pendingByConvId.delete(conversationId);
                    pending.reject(new Error(err?.body?.message || err?.message || 'Failed to publish request'));
                }
            });

        return responsePromise;
    }

    // ── Suggestions (welcome chips) ───────────────────────────────────────────
    get suggestions() {
        // On a record page with known object-specific prompts, prefer those.
        const recordPrompts = this.recordPrompts;
        if (recordPrompts) return recordPrompts;

        return this.mode === MODE_AGENTS
            ? ['Show all accounts', "What's in my pipeline?", 'List expiring contracts', 'Show Closed Won opportunities']
            : ['Summarize my open cases', "What's in my pipeline?", 'Draft a follow-up email', 'Show top opportunities'];
    }

    // Returns object-specific prompts with the record's display name
    // interpolated into each template's {name} placeholder.
    //
    // While the record wire is loading, falls back to "this <object>" so the
    // chips work immediately instead of flashing empty values.
    get recordPrompts() {
        if (!this.objectApiName) return null;
        const templates = RECORD_PROMPTS[this.objectApiName];
        if (!templates) return null;

        const name = this.recordDisplayName
            || `this ${OBJECT_LABELS[this.objectApiName] || 'record'}`;

        return templates.map((tpl) => tpl.replace(/\{name\}/g, name));
    }

    get isOnRecord() {
        return !!this.objectApiName;
    }

    get agentPills() {
        return Object.entries(AGENT_META).map(([name, meta]) => ({
            name,
            label:     meta.label,
            icon:      meta.icon,
            pillClass: `agent-pill ${meta.color}`,
        }));
    }

    // ── Computed ──────────────────────────────────────────────────────────────
    get showWelcome()    { return this.uiMessages.length === 0; }
    get sendDisabled()   { return this.isLoading || !this.inputValue.trim(); }
    get isAgentMode()    { return this.mode === MODE_AGENTS; }
    get containerClass() {
        if (this.isMinimized) return 'chat-container minimized';
        return this.isMaximized ? 'chat-container open maximized' : 'chat-container open';
    }
    get chatWindowClass() { return this.isMaximized ? 'chat-window chat-window-max' : 'chat-window'; }
    get maximizeIcon()    { return this.isMaximized ? 'utility:contract_alt' : 'utility:expand_alt'; }
    get maximizeTitle()   { return this.isMaximized ? 'Restore' : 'Maximize'; }
    get chatModeClass()  { return `mode-btn${this.mode === MODE_CHAT   ? ' mode-active' : ''}`; }
    get agentModeClass() { return `mode-btn${this.mode === MODE_AGENTS ? ' mode-active' : ''}`; }
    get modeHint()       { return this.mode === MODE_AGENTS ? 'Accounts · Opportunities · Contracts' : 'General AI assistant'; }
    get welcomeSub() {
        if (this.isOnRecord) {
            const label = OBJECT_LABELS[this.objectApiName] || 'record';
            const name = this.recordDisplayName;
            return name
                ? `I can help with ${name}. Pick a prompt below or ask your own.`
                : `I can help with this ${label}. Pick a prompt below or ask your own.`;
        }
        return this.mode === MODE_AGENTS
            ? "I'll route your question to the right Salesforce specialist."
            : 'Your AI-powered assistant. Ask me anything.';
    }
    get inputLabel()     { return this.mode === MODE_AGENTS ? 'Ask a Salesforce agent' : 'Ask Einstein'; }
    get inputPlaceholder() { return this.mode === MODE_AGENTS ? 'e.g. Show expiring contracts…' : 'Ask Einstein anything…'; }

    // ── Mode toggle ───────────────────────────────────────────────────────────
    setModeChat(e)   { e.stopPropagation(); if (this.mode !== MODE_CHAT)   { this.mode = MODE_CHAT;   this.clearChat(); } }
    setModeAgents(e) { e.stopPropagation(); if (this.mode !== MODE_AGENTS) { this.mode = MODE_AGENTS; this.clearChat(); } }

    // ── Open / Minimize / Maximize ─────────────────────────────────────────────
    toggleChat() {
        this.isMinimized = !this.isMinimized;
        if (!this.isMinimized) { this.unreadCount = 0; this._scrollToBottom(); }
        // Collapsing back to the FAB always exits maximized state.
        if (this.isMinimized) this.isMaximized = false;
    }
    handleMinimize(e) { e.stopPropagation(); this.isMinimized = true; this.isMaximized = false; }

    // Maximize button lives inside the header (which has its own onclick to
    // minimize), so stop propagation to avoid toggling the chat closed.
    toggleMaximize(e) {
        e.stopPropagation();
        this.isMaximized = !this.isMaximized;
        this._scrollToBottom();
    }

    // Clicking the dimmed backdrop restores the windowed (non-maximized) size.
    handleBackdropClick() {
        this.isMaximized = false;
    }

    // ── Input ─────────────────────────────────────────────────────────────────
    handleInput(e)   { this.inputValue = e.target.value; }
    handleKeyDown(e) { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); this.sendMessage(); } }

    handleSuggestion(e) {
        this.inputValue = e.currentTarget.dataset.label;
        this.sendMessage();
    }

    clearChat() {
        this.history      = [];
        this.uiMessages   = [];
        this.errorMessage = null;
        this.unreadCount  = 0;
        this.inputValue   = '';
        this._clearTextarea();
    }

    // A bound <textarea>'s rendered content is its child text node, which the
    // `value` attribute binding does not reliably reset to empty. Clear the DOM
    // element directly so the box visibly empties after sending.
    _clearTextarea() {
        const ta = this.refs?.textarea;
        if (ta) {
            ta.value = '';
            ta.style.height = 'auto';   // reset any auto-grow height
        }
    }

    // ── Send ──────────────────────────────────────────────────────────────────
    async sendMessage() {
        const text = this.inputValue.trim();
        if (!text || this.isLoading) return;

        this.inputValue   = '';
        this._clearTextarea();
        this.errorMessage = null;
        this.isLoading    = true;

        // Hide suggestions on all previous messages when user sends a new one
        this.uiMessages = this.uiMessages.map(m => ({ ...m, showSuggestions: false }));

        this.uiMessages = [
            ...this.uiMessages,
            this._makeUiMsg('user', text),
            this._makeUiMsg('assistant', '', true),   // typing indicator
        ];
        this._scrollToBottom();

        // History sent to the subscriber is prior turns only — the subscriber
        // appends the current user message before calling the LLM.
        const priorHistoryJson = JSON.stringify(this.history);
        const conversationId   = newConversationId();

        try {
            await this._ensureSubscribed();   // defensive: re-subscribe if dropped

            const result = await this._awaitResponse(
                conversationId,
                this.mode,
                text,
                priorHistoryJson
            );

            if (this.mode === MODE_AGENTS) {
                this._applyAgentsResponse(result);
            } else {
                this._applyChatResponse(result);
            }
        } catch (err) {
            this.uiMessages   = this.uiMessages.filter(m => !m.typing);
            this.errorMessage = err.message || 'Could not reach Einstein. Please try again.';
        } finally {
            this.isLoading = false;
            this._scrollToBottom();
        }
    }

    _applyChatResponse(result) {
        // Expected: { replyText: string, updatedHistory: [{role, content}] }
        this.history = result.updatedHistory ?? this.history;
        const replyText = result.replyText ?? '';

        this.uiMessages = [
            ...this.uiMessages.filter(m => !m.typing),
            this._makeUiMsg('assistant', replyText),
        ];

        if (this.isMinimized) this.unreadCount += 1;
    }

    _applyAgentsResponse(result) {
        // Expected: {
        //   responses:      [{ agentName, answer }...],
        //   suggestions:    string[],
        //   updatedHistory: [{ role, content }...]
        // }
        this.history = result.updatedHistory ?? this.history;

        const nonTyping = this.uiMessages.filter(m => !m.typing);
        const responses = result.responses ?? [];
        const allSuggs  = result.suggestions ?? [];

        const agentBubbles = responses.map((r, idx, arr) => {
            const isLast = idx === arr.length - 1;
            return this._makeUiMsg(
                'assistant',
                r.answer,
                false,
                r.agentName,
                isLast ? allSuggs : []
            );
        });

        this.uiMessages = [...nonTyping, ...agentBubbles];
        if (this.isMinimized) this.unreadCount += agentBubbles.length;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    _makeUiMsg(role, text, typing = false, agentName = null, suggestions = []) {
        const isUser = role === 'user';
        const meta   = agentName ? AGENT_META[agentName] : null;

        return {
            id:              uid(),
            role,
            text,
            typing,
            agentName:       meta ? meta.label : null,
            initials:        isUser ? this.currentUserInitials : (meta ? meta.label.slice(0, 2).toUpperCase() : 'AI'),
            name:            isUser ? this.currentUserName : (meta ? meta.label : 'Einstein'),
            time:            typing ? null : formatTime(new Date()),
            wrapClass:       `msg-wrap ${isUser ? 'msg-user' : 'msg-assistant'}`,
            avatarClass:     `msg-avatar ${isUser ? 'avatar-user' : (meta ? `avatar-${meta.color}` : 'avatar-ai')}`,
            bubbleClass:     `msg-bubble ${isUser ? 'bubble-user' : 'bubble-assistant'}`,
            agentBadgeClass: `agent-badge ${meta ? meta.color : ''}`,
            suggestions,
            showSuggestions: !typing && !isUser && suggestions.length > 0,
        };
    }

    _scrollToBottom() {
        setTimeout(() => {
            const list = this.refs.messageList;
            if (list) list.scrollTop = list.scrollHeight;
        }, 50);
    }
}
