"""
Salesforce MCP Chatbot
======================
A conversational chatbot powered by LangChain that uses the Salesforce MCP server.

Unlike the agent (which is task-focused and autonomous), this chatbot:
  - Maintains full multi-turn conversation context
  - Answers follow-up questions about previous results
  - Provides a friendly, guided experience
  - Can handle clarifying questions and refine queries
  - Remembers context within the session

Usage:
    python salesforce_chatbot.py

Requirements:
    pip install langchain langchain-anthropic langchain-mcp-adapters mcp python-dotenv
"""

import asyncio
import os
import json
from pathlib import Path
from datetime import datetime
from dotenv import load_dotenv

from langchain_anthropic import ChatAnthropic
from langchain.agents import AgentExecutor, create_tool_calling_agent
from langchain_core.prompts import ChatPromptTemplate, MessagesPlaceholder
from langchain_core.messages import HumanMessage, AIMessage, SystemMessage
from langchain_mcp_adapters.client import MultiServerMCPClient

load_dotenv()

# ── Configuration ──────────────────────────────────────────────────────────────

MCP_SERVER_PATH = os.getenv(
    "MCP_SERVER_PATH",
    str(Path(__file__).parent / "SalesforceMcpServer" / "publish" / "SalesforceMcpServer")
)

ANTHROPIC_API_KEY = os.getenv("ANTHROPIC_API_KEY", "")
MODEL_NAME = os.getenv("MODEL_NAME", "claude-sonnet-4-20250514")

# ── System Prompt ──────────────────────────────────────────────────────────────

CHATBOT_SYSTEM_PROMPT = f"""You are a friendly and helpful Salesforce data assistant named "Savi" 
(Salesforce AI). You help users explore and manage their Salesforce data through conversation.

Today's date: {datetime.now().strftime('%B %d, %Y')}

Your personality:
- Friendly and conversational, but professional
- Proactively suggest related queries or actions
- Explain what you're doing in plain language
- Ask clarifying questions when the user's intent is unclear
- Summarize results clearly, highlighting key insights

Available Salesforce tools:
- SalesforceNaturalLanguage: The primary tool — use natural language for almost everything
- SalesforceSoqlQuery: Raw SOQL for precise queries
- SalesforceCreateRecord: Create new records
- SalesforceUpdateRecord: Update existing records by ID
- SalesforceDeleteRecord: Delete records by ID  
- SalesforceExportCsv: Export data to CSV file
- SalesforceListSchema: See available objects and fields
- SalesforceDescribeObject: Detailed info about a specific object
- SalesforceReloadSchemas: Refresh schema definitions

Conversation guidelines:
1. For the very first message, greet the user and ask what they'd like to do.
2. When showing record results, summarize key fields — don't dump all raw data unless asked.
3. If the user says "show me more" or "all of them", remove the limit and re-query.
4. Proactively offer related actions (e.g., after showing accounts: "Would you like to see their contacts or opportunities?")
5. For exports, always confirm the file was saved and show the path.
6. Before deleting anything, always confirm with the user by repeating the record details.
7. When a query returns 0 results, suggest alternatives.
8. Keep track of IDs from previous results so the user can say "update the first one" etc.

Safety rules:
- Never delete without explicit user confirmation
- Always validate IDs before update/delete operations
- Warn the user if they're about to modify a large number of records
"""

# ── Session Context ────────────────────────────────────────────────────────────

class ChatSession:
    """Maintains conversation state and context."""

    def __init__(self):
        self.history: list = []
        self.last_results: list = []  # Last query results for follow-up references
        self.last_object: str | None = None
        self.session_start = datetime.now()
        self.message_count = 0

    def add_exchange(self, user_msg: str, ai_msg: str):
        self.history.append(HumanMessage(content=user_msg))
        self.history.append(AIMessage(content=ai_msg))
        self.message_count += 1
        # Keep last 30 exchanges (60 messages) to stay within context window
        if len(self.history) > 60:
            self.history = self.history[-60:]

    def format_history_for_display(self) -> str:
        lines = [f"\nSession started: {self.session_start.strftime('%H:%M:%S')}"]
        lines.append(f"Messages: {self.message_count}\n")
        for msg in self.history[-10:]:  # Last 5 exchanges
            role = "You" if isinstance(msg, HumanMessage) else "Savi"
            content = msg.content[:150] + "..." if len(msg.content) > 150 else msg.content
            lines.append(f"  {role}: {content}")
        return "\n".join(lines)


# ── Chatbot Setup ──────────────────────────────────────────────────────────────

async def create_chatbot(tools: list) -> AgentExecutor:
    """Create the LangChain chatbot agent."""
    llm = ChatAnthropic(
        model=MODEL_NAME,
        api_key=ANTHROPIC_API_KEY,
        temperature=0.3,  # Slightly higher than agent for more natural conversation
    )

    prompt = ChatPromptTemplate.from_messages([
        ("system", CHATBOT_SYSTEM_PROMPT),
        MessagesPlaceholder(variable_name="chat_history"),
        ("human", "{input}"),
        MessagesPlaceholder(variable_name="agent_scratchpad"),
    ])

    agent = create_tool_calling_agent(llm, tools, prompt)
    return AgentExecutor(
        agent=agent,
        tools=tools,
        verbose=False,  # Less verbose for chatbot UX
        max_iterations=8,
        handle_parsing_errors=True,
    )


# ── UI Helpers ─────────────────────────────────────────────────────────────────

def print_banner():
    print("\n" + "=" * 65)
    print("  🤖  Savi - Salesforce AI Assistant")
    print("  Powered by Claude + LangChain + MCP")
    print("=" * 65)
    print("  Commands: 'history' | 'clear' | 'schema' | 'quit'")
    print("=" * 65 + "\n")


def print_thinking():
    print("  Savi is thinking...", end="\r")


def clear_thinking():
    print("                     ", end="\r")


def format_response(response: str) -> str:
    """Add visual formatting to responses."""
    lines = response.strip().split("\n")
    formatted = []
    for line in lines:
        if line.strip():
            formatted.append(f"  {line}")
        else:
            formatted.append("")
    return "\n".join(formatted)


# ── Main Chatbot Loop ──────────────────────────────────────────────────────────

async def run_chatbot():
    """Start the MCP server and run the interactive chatbot loop."""
    print_banner()
    print(f"  Connecting to Salesforce MCP server...")

    async with MultiServerMCPClient(
        {
            "salesforce": {
                "command": MCP_SERVER_PATH,
                "args": [],
                "transport": "stdio",
            }
        }
    ) as client:
        tools = client.get_tools()
        print(f"  ✓ Connected! {len(tools)} tools available.\n")

        chatbot = await create_chatbot(tools)
        session = ChatSession()

        # Initial greeting
        greeting_result = await chatbot.ainvoke({
            "input": "Hello, please introduce yourself briefly and ask how you can help with Salesforce today.",
            "chat_history": [],
        })
        greeting = greeting_result.get("output", "Hello! How can I help you with Salesforce today?")
        session.add_exchange("[session start]", greeting)
        print(f"Savi: {format_response(greeting)}\n")

        # Main loop
        while True:
            try:
                user_input = input("You: ").strip()
            except (EOFError, KeyboardInterrupt):
                print("\n\nSavi: Goodbye! Have a great day! 👋\n")
                break

            if not user_input:
                continue

            # Built-in commands
            if user_input.lower() in {"quit", "exit", "bye", "goodbye"}:
                farewell_result = await chatbot.ainvoke({
                    "input": "The user is leaving. Say goodbye warmly.",
                    "chat_history": session.history,
                })
                print(f"\nSavi: {format_response(farewell_result.get('output', 'Goodbye!'))}\n")
                break

            elif user_input.lower() == "history":
                print(session.format_history_for_display())
                continue

            elif user_input.lower() == "clear":
                session = ChatSession()
                print("\n  [Conversation cleared]\n")
                continue

            elif user_input.lower() in {"schema", "list schema", "what objects"}:
                user_input = "List all available Salesforce objects and give me a brief summary."

            # Send to chatbot
            print()
            print_thinking()
            try:
                result = await chatbot.ainvoke({
                    "input": user_input,
                    "chat_history": session.history,
                })
                clear_thinking()
                output = result.get("output", "I'm not sure how to help with that.")
                session.add_exchange(user_input, output)
                print(f"Savi: {format_response(output)}\n")

            except Exception as e:
                clear_thinking()
                error_msg = f"I encountered an error: {str(e)[:200]}. Please try rephrasing your request."
                print(f"Savi: {format_response(error_msg)}\n")


# ── Multi-turn Demo ────────────────────────────────────────────────────────────

async def run_demo_conversation():
    """
    Demonstrate a realistic multi-turn conversation with the chatbot.
    Shows how context is maintained across turns.
    """
    demo_conversation = [
        "What Salesforce objects do I have access to?",
        "Show me the top 5 accounts by annual revenue",
        "Now show me the opportunities for the first account",
        "Export those opportunities to /tmp/demo_opps.csv",
        "How many total contacts are there?",
        "Find contacts in California",
        "Create a new lead: Jane Smith at MegaCorp, email jane@megacorp.com",
    ]

    print_banner()
    print("  Running demo conversation...\n")

    async with MultiServerMCPClient(
        {
            "salesforce": {
                "command": MCP_SERVER_PATH,
                "args": [],
                "transport": "stdio",
            }
        }
    ) as client:
        tools = client.get_tools()
        chatbot = await create_chatbot(tools)
        session = ChatSession()

        for turn, user_msg in enumerate(demo_conversation, 1):
            print(f"\n{'─' * 55}")
            print(f"  Turn {turn}")
            print(f"  You: {user_msg}")
            print(f"{'─' * 55}")

            try:
                result = await chatbot.ainvoke({
                    "input": user_msg,
                    "chat_history": session.history,
                })
                output = result.get("output", "...")
                session.add_exchange(user_msg, output)
                print(f"  Savi: {format_response(output)}")
            except Exception as e:
                print(f"  Error: {e}")

            await asyncio.sleep(0.5)  # Small pause between turns


# ── Entry Point ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import sys

    if len(sys.argv) > 1 and sys.argv[1] == "--demo":
        asyncio.run(run_demo_conversation())
    else:
        asyncio.run(run_chatbot())
