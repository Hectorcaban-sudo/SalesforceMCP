"""
Salesforce MCP Agent
====================
An autonomous agent powered by LangChain that uses the Salesforce MCP server
to perform multi-step Salesforce tasks.

The agent can:
  - Understand complex natural language goals
  - Decompose them into multiple tool calls
  - Reason through results and take follow-up actions
  - Create CSV exports, perform CRUD operations, etc.

Usage:
    python salesforce_agent.py

Requirements:
    pip install langchain langchain-anthropic langchain-mcp-adapters mcp python-dotenv
"""

import asyncio
import os
from pathlib import Path
from dotenv import load_dotenv

from langchain_anthropic import ChatAnthropic
from langchain.agents import AgentExecutor, create_tool_calling_agent
from langchain_core.prompts import ChatPromptTemplate, MessagesPlaceholder
from langchain_core.messages import HumanMessage, AIMessage
from langchain_mcp_adapters.client import MultiServerMCPClient

load_dotenv()

# ── Configuration ──────────────────────────────────────────────────────────────

# Path to your compiled MCP server executable.
# After building: dotnet publish -c Release -o ./publish
MCP_SERVER_PATH = os.getenv(
    "MCP_SERVER_PATH",
    str(Path(__file__).parent / "SalesforceMcpServer" / "publish" / "SalesforceMcpServer")
)

ANTHROPIC_API_KEY = os.getenv("ANTHROPIC_API_KEY", "")
MODEL_NAME = os.getenv("MODEL_NAME", "claude-sonnet-4-20250514")

# ── System Prompt ──────────────────────────────────────────────────────────────

AGENT_SYSTEM_PROMPT = """You are an expert Salesforce data assistant with access to a Salesforce 
MCP server. You help users interact with their Salesforce data through natural language.

You have access to these tools:
- SalesforceNaturalLanguage: Use natural language to query/create/update/delete/export data
- SalesforceSoqlQuery: Run raw SOQL queries for precise control
- SalesforceCreateRecord: Create new Salesforce records with structured field data
- SalesforceUpdateRecord: Update existing records by ID
- SalesforceDeleteRecord: Delete records by ID
- SalesforceExportCsv: Export query results to a CSV file
- SalesforceListSchema: List all available Salesforce objects and fields
- SalesforceDescribeObject: Get details about a specific object's fields
- SalesforceReloadSchemas: Reload schema files without restarting

Guidelines:
1. Always start by checking available schemas if you're unsure about object/field names.
2. For complex requests, break them into steps and use the right tool for each.
3. When exporting data, confirm the file path with the user.
4. For destructive operations (delete/update), confirm the record ID before proceeding.
5. Present results in a clear, readable format.
6. If a query returns no results, suggest refining the search criteria.
"""

# ── Agent Setup ────────────────────────────────────────────────────────────────

async def create_agent(tools: list):
    """Create the LangChain tool-calling agent."""
    llm = ChatAnthropic(
        model=MODEL_NAME,
        api_key=ANTHROPIC_API_KEY,
        temperature=0,
    )

    prompt = ChatPromptTemplate.from_messages([
        ("system", AGENT_SYSTEM_PROMPT),
        MessagesPlaceholder(variable_name="chat_history", optional=True),
        ("human", "{input}"),
        MessagesPlaceholder(variable_name="agent_scratchpad"),
    ])

    agent = create_tool_calling_agent(llm, tools, prompt)
    return AgentExecutor(
        agent=agent,
        tools=tools,
        verbose=True,
        max_iterations=10,
        handle_parsing_errors=True,
    )


# ── Main Agent Loop ────────────────────────────────────────────────────────────

async def run_agent():
    """Start the MCP server and run the interactive agent loop."""
    print("=" * 60)
    print("  Salesforce MCP Agent")
    print("=" * 60)
    print(f"  Connecting to MCP server: {MCP_SERVER_PATH}")
    print("  Type 'quit' or 'exit' to stop.\n")

    # Connect to the MCP server via stdio
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
        print(f"  Loaded {len(tools)} tools from MCP server.\n")

        agent_executor = await create_agent(tools)

        chat_history = []

        # ── Example autonomous tasks (uncomment to run) ────────────────────
        # example_tasks = [
        #     "First list all available Salesforce objects, then find the top 5 accounts by annual revenue",
        #     "Find all open opportunities over $100,000 and export them to /tmp/big_deals.csv",
        #     "Create a new lead named Jane Doe at TechCorp with email jane@techcorp.com, then confirm it was created",
        # ]
        # for task in example_tasks:
        #     print(f"\n>> Autonomous Task: {task}")
        #     result = await agent_executor.ainvoke({"input": task, "chat_history": chat_history})
        #     print(f"\nAgent Result: {result['output']}\n")
        # ──────────────────────────────────────────────────────────────────

        # Interactive loop
        while True:
            try:
                user_input = input("\nYou: ").strip()
            except (EOFError, KeyboardInterrupt):
                print("\nGoodbye!")
                break

            if not user_input:
                continue
            if user_input.lower() in {"quit", "exit", "bye"}:
                print("Goodbye!")
                break

            try:
                result = await agent_executor.ainvoke({
                    "input": user_input,
                    "chat_history": chat_history,
                })
                output = result.get("output", "No response.")
                print(f"\nAgent: {output}")

                # Maintain conversation history
                chat_history.append(HumanMessage(content=user_input))
                chat_history.append(AIMessage(content=output))

                # Keep history manageable (last 20 exchanges)
                if len(chat_history) > 40:
                    chat_history = chat_history[-40:]

            except Exception as e:
                print(f"\nError: {e}")


# ── Example Standalone Tasks ───────────────────────────────────────────────────

async def run_demo_tasks():
    """
    Run a set of pre-defined demo tasks to showcase the agent's capabilities.
    Useful for testing without interactive input.
    """
    demo_tasks = [
        "List all available Salesforce objects and their field counts.",
        "Show me the top 10 accounts sorted by annual revenue.",
        "Find all open opportunities and export them to /tmp/open_opps.csv",
        "Create a new lead: first name = 'Alice', last name = 'Johnson', company = 'Innovatech', email = 'alice@innovatech.com', status = 'New'",
        "How many contacts are there in total?",
    ]

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
        agent_executor = await create_agent(tools)

        for i, task in enumerate(demo_tasks, 1):
            print(f"\n{'='*60}")
            print(f"Demo Task {i}: {task}")
            print("=" * 60)
            try:
                result = await agent_executor.ainvoke({"input": task, "chat_history": []})
                print(f"Result: {result['output']}")
            except Exception as e:
                print(f"Error: {e}")


# ── Entry Point ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import sys

    if len(sys.argv) > 1 and sys.argv[1] == "--demo":
        asyncio.run(run_demo_tasks())
    else:
        asyncio.run(run_agent())
