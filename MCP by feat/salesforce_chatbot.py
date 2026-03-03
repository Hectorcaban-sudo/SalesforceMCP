#!/usr/bin/env python3
"""
salesforce_chatbot.py
A conversational LangChain chatbot with memory that uses the Salesforce MCP server.
Unlike the agent, this maintains conversation history and handles follow-up questions naturally.

Usage:
    python salesforce_chatbot.py
    python salesforce_chatbot.py --provider anthropic
"""

import argparse
import json
import os
import sys
from typing import Any
from dotenv import load_dotenv
from rich.console import Console
from rich.panel import Panel
from rich.markdown import Markdown
from rich.spinner import Spinner
from rich.live import Live

load_dotenv()

from langchain.agents import AgentExecutor, create_openai_tools_agent
from langchain.memory import ConversationBufferWindowMemory
from langchain.prompts import ChatPromptTemplate, MessagesPlaceholder
from langchain_core.messages import SystemMessage
from langchain_openai import ChatOpenAI
from langchain_anthropic import ChatAnthropic

from mcp_tools import build_langchain_tools, initialize_mcp

console = Console()

# ── System prompt ─────────────────────────────────────────────────────────────

CHATBOT_SYSTEM = """You are a helpful Salesforce assistant integrated with a live Salesforce org 
via an MCP server. You can perform full CRUD operations and data exports.

Your capabilities:
- 🔍 **Query**: "Show me all accounts", "Find contacts at Acme Corp", "Get open opportunities"
- ➕ **Create**: "Add a new lead named John Smith from Acme", "Create a contact..."
- ✏️ **Update**: "Change the phone number for account ID 001xxx...", "Update the status to Closed Won"
- 🗑️ **Delete**: "Delete the account with ID 001xxx..." (always confirm first)
- 📊 **Export**: "Export all accounts to CSV", "Download contacts as a spreadsheet"
- 📋 **Schema**: "What fields does the Opportunity object have?", "List available objects"

Conversation style:
- Be concise and professional
- When showing records, format them clearly
- If you need a record ID to update/delete, offer to search for it first
- Proactively suggest related actions (e.g., after creating a record, offer to view it)
- Remember the conversation context - if the user says "update that one", you know what they mean
- Before deleting anything, always confirm with the user

If an operation fails, explain why and suggest alternatives."""


# ── Chatbot builder ───────────────────────────────────────────────────────────

def build_chatbot(provider: str = "openai"):
    tools = build_langchain_tools()

    if provider == "anthropic":
        llm = ChatAnthropic(
            model=os.environ.get("ANTHROPIC_MODEL", "claude-3-5-sonnet-20241022"),
            temperature=0.1,
            api_key=os.environ.get("ANTHROPIC_API_KEY")
        )
    else:
        llm = ChatOpenAI(
            model=os.environ.get("OPENAI_MODEL", "gpt-4o"),
            temperature=0.1,
            api_key=os.environ.get("OPENAI_API_KEY")
        )

    # OpenAI-style tool-use agent with memory
    prompt = ChatPromptTemplate.from_messages([
        SystemMessage(content=CHATBOT_SYSTEM),
        MessagesPlaceholder(variable_name="chat_history"),
        ("human", "{input}"),
        MessagesPlaceholder(variable_name="agent_scratchpad"),
    ])

    agent = create_openai_tools_agent(llm=llm, tools=tools, prompt=prompt)

    memory = ConversationBufferWindowMemory(
        memory_key="chat_history",
        return_messages=True,
        k=20  # Keep last 20 exchanges
    )

    return AgentExecutor(
        agent=agent,
        tools=tools,
        memory=memory,
        verbose=False,
        max_iterations=8,
        handle_parsing_errors=True,
    )


# ── Formatting helpers ────────────────────────────────────────────────────────

def format_records_table(data: str) -> str:
    """Try to format JSON record lists as a readable summary."""
    try:
        parsed = json.loads(data)
        if isinstance(parsed, dict) and "data" in parsed:
            inner = parsed["data"]
            if isinstance(inner, dict) and "records" in inner:
                records = inner["records"]
                if records and len(records) > 0:
                    count = parsed.get("totalSize", len(records))
                    return f"Found {count} record(s). First result: {json.dumps(records[0], indent=2)}"
    except Exception:
        pass
    return data


def render_response(response: str):
    """Render assistant response with rich formatting."""
    if response.startswith("{") or response.startswith("["):
        try:
            parsed = json.loads(response)
            console.print_json(json.dumps(parsed))
            return
        except Exception:
            pass

    console.print(Markdown(response))


# ── Session state ─────────────────────────────────────────────────────────────

class ChatSession:
    def __init__(self, provider: str = "openai"):
        self.agent_executor = build_chatbot(provider)
        self.message_count = 0
        self.last_records: list[dict] = []

    def chat(self, user_input: str) -> str:
        self.message_count += 1
        result = self.agent_executor.invoke({"input": user_input})
        output = result.get("output", "I couldn't process that request.")
        return output

    def reset(self):
        """Clear conversation memory."""
        self.agent_executor.memory.clear()
        self.message_count = 0
        console.print("[dim]Conversation memory cleared.[/dim]")


# ── CLI ───────────────────────────────────────────────────────────────────────

HELP_TEXT = """
Commands:
  /help          - Show this help
  /reset         - Clear conversation memory
  /history       - Show conversation history  
  /objects       - List available Salesforce objects
  quit / exit    - Exit the chatbot

Examples:
  "Show me all accounts in the Technology industry"
  "Find contacts whose email contains @gmail.com"
  "Create a new Account named Demo Corp with Phone 555-0000"
  "Export all contacts to CSV"
  "What fields are on the Opportunity object?"
"""

def run_chatbot(provider: str = "openai"):
    console.print(Panel(
        "[bold]Salesforce Chatbot[/bold]\n\n"
        "Chat naturally with your Salesforce data.\n"
        "I remember our conversation and can handle follow-up questions.\n\n"
        "Type [bold]/help[/bold] for commands or [bold]quit[/bold] to exit.",
        title="[bold blue]💼 Salesforce Assistant[/bold blue]",
        border_style="blue"
    ))

    session = ChatSession(provider)

    while True:
        try:
            user_input = console.input("\n[bold cyan]You:[/bold cyan] ").strip()
            if not user_input:
                continue

            # Handle commands
            if user_input.lower() in ("quit", "exit", "q", "/quit"):
                console.print("[dim]Goodbye! Have a productive day.[/dim]")
                break
            elif user_input.lower() in ("/help", "help"):
                console.print(Panel(HELP_TEXT, title="Help"))
                continue
            elif user_input.lower() == "/reset":
                session.reset()
                continue
            elif user_input.lower() == "/objects":
                user_input = "List all available Salesforce objects"
            elif user_input.lower() == "/history":
                memory = session.agent_executor.memory.chat_memory.messages
                console.print(f"[dim]Conversation has {len(memory)} messages[/dim]")
                continue

            # Process message with spinner
            with Live(Spinner("dots", text="Thinking..."), refresh_per_second=10, console=console):
                response = session.chat(user_input)

            console.print(f"\n[bold green]Assistant:[/bold green]")
            render_response(response)

        except KeyboardInterrupt:
            console.print("\n[dim]Interrupted. Type 'quit' to exit.[/dim]")
        except Exception as e:
            console.print(f"\n[bold red]Error:[/bold red] {e}")
            if os.environ.get("DEBUG"):
                import traceback
                traceback.print_exc()


# ── Entry point ───────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Salesforce LangChain Chatbot")
    parser.add_argument("--provider", default=os.environ.get("LLM_PROVIDER", "openai"),
                        choices=["openai", "anthropic"], help="LLM provider")
    args = parser.parse_args()

    # Connect to MCP server
    try:
        initialize_mcp()
    except Exception as e:
        console.print(f"[bold red]Failed to connect to MCP server:[/bold red] {e}")
        console.print(f"[dim]Ensure the C# MCP server is running at: "
                      f"{os.environ.get('MCP_SERVER_URL', 'http://localhost:5000/mcp')}[/dim]")
        sys.exit(1)

    run_chatbot(args.provider)


if __name__ == "__main__":
    main()
