import { LightningElement, track } from 'lwc';

const API_ENDPOINT = 'https://YOUR-API-HOST/api/salesforce/agent-chat';

let msgId = 0;

export default class EinsteinChat extends LightningElement {
    @track uiMessages = [];
    @track inputValue = '';
    @track isLoading = false;
    @track errorMessage = '';

    history = [];

    get sendDisabled() {
        return this.isLoading || !this.inputValue.trim();
    }

    handleInput(event) {
        this.inputValue = event.target.value;
    }

    handleKeyDown(event) {
        if (event.key === 'Enter' && !event.shiftKey) {
            event.preventDefault();
            this.sendMessage();
        }
    }

    async sendMessage() {
        const userText = this.inputValue.trim();
        if (!userText || this.isLoading) return;

        this.errorMessage = '';
        this.inputValue = '';
        this.history = [...this.history, { role: 'user', content: userText }];
        this.uiMessages = [...this.uiMessages, this.makeUiMessage('user', userText)];
        this.isLoading = true;

        try {
            const response = await fetch(API_ENDPOINT, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(this.history)
            });

            const payload = await response.json();
            if (!response.ok) {
                throw new Error(payload.error || `Request failed (${response.status})`);
            }

            this.history = payload.updatedHistory || this.history;

            const rows = (payload.agentResponses || []).map(a =>
                `${a.agentName}\nSOQL: ${a.soql}\n${a.explanation}`
            );

            this.uiMessages = [...this.uiMessages, this.makeUiMessage('assistant', rows.join('\n\n'))];
        } catch (error) {
            this.errorMessage = error.message;
        } finally {
            this.isLoading = false;
        }
    }

    makeUiMessage(role, text) {
        msgId += 1;
        return {
            id: `msg-${msgId}`,
            role,
            text,
            cssClass: role === 'user' ? 'msg user' : 'msg assistant'
        };
    }
}
