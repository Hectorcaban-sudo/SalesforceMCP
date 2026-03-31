import { LightningElement, track } from 'lwc';

const BASE_URL            = 'https://YOUR-API-HOST';
const CHAT_ENDPOINT       = `${BASE_URL}/api/chat`;
const AGENT_CHAT_ENDPOINT = `${BASE_URL}/api/agent-chat`;

const MODE_CHAT   = 'chat';
const MODE_AGENTS = 'agents';

const AGENT_META = {
    AccountsAgent:      { label: 'Accounts',     icon: 'utility:account',     color: 'agent-blue'   },
    OpportunitiesAgent: { label: 'Opportunities', icon: 'utility:opportunity', color: 'agent-green'  },
    ContractsAgent:     { label: 'Contracts',     icon: 'utility:contract',    color: 'agent-purple' },
};

let msgIdCounter = 0;
const uid = () => `msg-${++msgIdCounter}`;

function formatTime(date) {
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export default class EinsteinChat extends LightningElement {

    @track uiMessages    = [];
    @track inputValue    = '';
    @track isLoading     = false;
    @track errorMessage  = null;
    @track isMinimized   = true;
    @track unreadCount   = 0;
    @track mode          = MODE_CHAT;

    history = [];

    // ── Suggestions (welcome chips) ───────────────────────────────────────────
    get suggestions() {
        return this.mode === MODE_AGENTS
            ? ['Show all accounts', "What's in my pipeline?", 'List expiring contracts', 'Show Closed Won opportunities']
            : ['Summarize my open cases', "What's in my pipeline?", 'Draft a follow-up email', 'Show top opportunities'];
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
    get containerClass() { return this.isMinimized ? 'chat-container minimized' : 'chat-container open'; }
    get chatModeClass()  { return `mode-btn${this.mode === MODE_CHAT   ? ' mode-active' : ''}`; }
    get agentModeClass() { return `mode-btn${this.mode === MODE_AGENTS ? ' mode-active' : ''}`; }
    get modeHint()       { return this.mode === MODE_AGENTS ? 'Accounts · Opportunities · Contracts' : 'General AI assistant'; }
    get welcomeSub()     { return this.mode === MODE_AGENTS ? "I'll route your question to the right Salesforce specialist." : 'Your AI-powered assistant. Ask me anything.'; }
    get inputLabel()     { return this.mode === MODE_AGENTS ? 'Ask a Salesforce agent' : 'Ask Einstein'; }
    get inputPlaceholder() { return this.mode === MODE_AGENTS ? 'e.g. Show expiring contracts…' : 'Ask Einstein anything…'; }

    // ── Mode toggle ───────────────────────────────────────────────────────────
    setModeChat(e)   { e.stopPropagation(); if (this.mode !== MODE_CHAT)   { this.mode = MODE_CHAT;   this.clearChat(); } }
    setModeAgents(e) { e.stopPropagation(); if (this.mode !== MODE_AGENTS) { this.mode = MODE_AGENTS; this.clearChat(); } }

    // ── Open / Minimize ───────────────────────────────────────────────────────
    toggleChat() {
        this.isMinimized = !this.isMinimized;
        if (!this.isMinimized) { this.unreadCount = 0; this._scrollToBottom(); }
    }
    handleMinimize(e) { e.stopPropagation(); this.isMinimized = true; }

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
    }

    // ── Send ──────────────────────────────────────────────────────────────────
    async sendMessage() {
        const text = this.inputValue.trim();
        if (!text || this.isLoading) return;

        this.inputValue   = '';
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

        const newHistory = [...this.history, { role: 'user', content: text }];

        try {
            if (this.mode === MODE_AGENTS) {
                await this._sendToAgents(newHistory);
            } else {
                await this._sendToChat(newHistory);
            }
        } catch (err) {
            this.uiMessages   = this.uiMessages.filter(m => !m.typing);
            this.errorMessage = err.message || 'Could not reach Einstein. Please try again.';
        } finally {
            this.isLoading = false;
            this._scrollToBottom();
        }
    }

    // ── /api/chat ─────────────────────────────────────────────────────────────
    async _sendToChat(newHistory) {
        const res = await fetch(CHAT_ENDPOINT, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(newHistory),
        });

        if (!res.ok) {
            const data = await res.json().catch(() => ({}));
            throw new Error(data.error || `HTTP ${res.status}`);
        }

        // Returns: List<ChatMessageDto>
        const updatedHistory = await res.json();
        this.history = updatedHistory;

        const replyText = updatedHistory[updatedHistory.length - 1]?.content ?? '';

        this.uiMessages = [
            ...this.uiMessages.filter(m => !m.typing),
            this._makeUiMsg('assistant', replyText),
        ];

        if (this.isMinimized) this.unreadCount += 1;
    }

    // ── /api/agent-chat ───────────────────────────────────────────────────────
    async _sendToAgents(newHistory) {
        const res = await fetch(AGENT_CHAT_ENDPOINT, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(newHistory),
        });

        if (!res.ok) {
            const data = await res.json().catch(() => ({}));
            throw new Error(data.error || `HTTP ${res.status}`);
        }

        // Returns: AgentChatResponseDto
        // {
        //   responses:      [{ agentName, answer, suggestions, rawContent }]
        //   suggestions:    string[]   ← aggregated cross-agent follow-ups (max 6)
        //   updatedHistory: [{ role, content }]
        // }
        const result  = await res.json();
        this.history  = result.updatedHistory ?? newHistory;

        const nonTyping = this.uiMessages.filter(m => !m.typing);

        // Build one bubble per agent response
        // The LAST agent bubble gets the aggregated suggestions shown below it
        const agentBubbles = (result.responses ?? []).map((r, idx, arr) => {
            const isLast    = idx === arr.length - 1;
            const allSuggs  = result.suggestions ?? [];
            return this._makeUiMsg(
                'assistant',
                r.answer,          // clean answer — suggestions stripped by server
                false,
                r.agentName,
                isLast ? allSuggs : []   // only show chips below the last bubble
            );
        });

        this.uiMessages = [...nonTyping, ...agentBubbles];

        if (this.isMinimized) this.unreadCount += agentBubbles.length;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    /**
     * @param {string} role
     * @param {string} text
     * @param {boolean} typing
     * @param {string|null} agentName   - raw agent key e.g. "AccountsAgent"
     * @param {string[]} suggestions    - follow-up prompts to show below this bubble
     */
    _makeUiMsg(role, text, typing = false, agentName = null, suggestions = []) {
        const isUser = role === 'user';
        const meta   = agentName ? AGENT_META[agentName] : null;

        return {
            id:             uid(),
            role,
            text,
            typing,
            agentName:      meta ? meta.label : null,
            initials:       isUser ? 'JD' : (meta ? meta.label.slice(0, 2).toUpperCase() : 'AI'),
            name:           isUser ? 'John Doe' : (meta ? meta.label : 'Einstein'),
            time:           typing ? null : formatTime(new Date()),
            wrapClass:      `msg-wrap ${isUser ? 'msg-user' : 'msg-assistant'}`,
            avatarClass:    `msg-avatar ${isUser ? 'avatar-user' : (meta ? `avatar-${meta.color}` : 'avatar-ai')}`,
            bubbleClass:    `msg-bubble ${isUser ? 'bubble-user' : 'bubble-assistant'}`,
            agentBadgeClass:`agent-badge ${meta ? meta.color : ''}`,
            // Suggestion chips
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
