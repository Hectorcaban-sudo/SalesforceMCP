import { LightningElement, api, track } from 'lwc';

let msgId = 0;

export default class SalesforceAgentChat extends LightningElement {
    @api apiEndpoint = 'https://YOUR-API-HOST/api/salesforce/agent-chat';

    @track uiMessages = [];
    @track inputValue = '';
    @track isLoading = false;
    @track errorMessage = '';

    history = [];

    get sendDisabled() {
        return this.isLoading || !this.inputValue?.trim();
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
        const userText = (this.inputValue || '').trim();
        if (!userText || this.isLoading) {
            return;
        }

        this.errorMessage = '';
        this.inputValue = '';
        this.history = [...this.history, { role: 'user', content: userText }];
        this.uiMessages = [...this.uiMessages, this.makeUiMessage('user', userText)];
        this.isLoading = true;

        try {
            const response = await fetch(this.apiEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(this.history)
            });

            const payload = await response.json();
            if (!response.ok) {
                throw new Error(payload.error || `Request failed (${response.status})`);
            }

            this.history = payload.updatedHistory || this.history;
            const responseText = (payload.agentResponses || [])
                .map((agentResponse) =>
                    `${agentResponse.agentName}\nSOQL: ${agentResponse.soql}\n${agentResponse.explanation}`)
                .join('\n\n');

            this.uiMessages = [...this.uiMessages, this.makeUiMessage('assistant', responseText)];
        } catch (error) {
            this.errorMessage = error.message || 'Unknown error while calling the chat API.';
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
            cssClass: role === 'user' ? 'message user' : 'message assistant'
        };
    }
}
