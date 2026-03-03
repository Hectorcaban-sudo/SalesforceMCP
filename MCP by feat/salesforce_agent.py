#!/usr/bin/env python3
"""
salesforce_agent.py
A LangChain ReAct agent that uses the custom Salesforce MCP server tools.
The agent can autonomously plan multi-step Salesforce operations.

Usage:
    python salesforce_agent.py
    python salesforce_agent.py --task "Find all accounts in technology industry and export them to CSV"
    python salesforce_agent.py --provider anthropic
"""

import argparse
import os
import sys
from dotenv import load_dotenv
from rich.console import Console
from rich.panel import Panel
from rich.markdown import Markdown

load_dotenv()

from langchain.agents import AgentExecutor, create_react_agent
from langchain.prompts import PromptTemplate
from langchain_openai import ChatOpenAI
from langchain_anthropic import ChatAnthropic

from mcp_tools import build_langchain_tools, initialize_mcp

console = Console()

# ── System prompt ─────────────────────────────────────────────────────────────

AGENT_SYSTEM_PROMPT = """You are an expert Salesforce assistant with access to tools that 
communicate with a Salesforce MCP server.

You can perform the following operations:
- **Query**: Search and retrieve Salesforce records using natural language
- **Create**: Add new records to Salesforce objects
- **Update**: Modify existing records by ID
- **Delete**: Remove records (always confirm before deleting)
- **Export**: Download records as CSV files
- **Describe**: Inspect object schemas to understand available fields
- **List Objects**: See all available Salesforce objects

IMPORTANT GUIDELINES:
1. Always use salesforce_list_objects or salesforce_describe first if you're unsure about available fields
2. For updates and deletes, you must have the record ID - query first if needed
3. Never delete without explicit user confirmation
4. When exporting, inform the user where the CSV was saved
5. If a query returns no results, suggest alternative search terms

Tools available:
{tools}

Tool names: {tool_names}

Use this format:

Question: the input question you must answer
Thought: you should always think about what to do
Action: the action to take, should be one of [{tool_names}]
Action Input: the input to the action
Observation: the result of the action
... (this Thought/Action/Action Input/Observation can repeat N times)
Thought: I now know the final answer
Final Answer: the final answer to the original input question

Question: {input}
Thought:{agent_scratchpad}"""


# ── Agent builder ─────────────────────────────────────────────────────────────

def build_agent(provider: str = "openai"):
    tools = build_langchain_tools()

    if provider == "anthropic":
        llm = ChatAnthropic(
            model=os.environ.get("ANTHROPIC_MODEL", "claude-3-5-sonnet-20241022"),
            temperature=0,
            api_key=os.environ.get("ANTHROPIC_API_KEY")
        )
    else:
        llm = ChatOpenAI(
            model=os.environ.get("OPENAI_MODEL", "gpt-4o"),
            temperature=0,
            api_key=os.environ.get("OPENAI_API_KEY")
        )

    prompt = PromptTemplate.from_template(AGENT_SYSTEM_PROMPT)
    agent = create_react_agent(llm=llm, tools=tools, prompt=prompt)

    return AgentExecutor(
        agent=agent,
        tools=tools,
        verbose=True,
        max_iterations=10,
        handle_parsing_errors=True,
        return_intermediate_steps=True,
    )


# ── Demo tasks ────────────────────────────────────────────────────────────────

DEMO_TASKS = [
    "List all available Salesforce objects",
    "Show me the fields available on the Account object",
    "Find the top 5 accounts by annual revenue",
    "Get all contacts with a gmail email address",
    "Create a new Account named 'Test Corp' with Industry=Technology and Phone=555-1234",
    "Export all accounts in the Technology industry to a CSV file",
    "Find all leads created this year and export them",
]


def run_agent(task: str, provider: str = "openai"):
    console.print(Panel(f"[bold cyan]Task:[/bold cyan] {task}", expand=False))

    try:
        agent = build_agent(provider)
        result = agent.invoke({"input": task})

        console.print("\n")
        console.print(Panel(
            Markdown(f"**Final Answer:**\n\n{result['output']}"),
            title="[bold green]Agent Result[/bold green]",
            expand=False
        ))
        return result

    except Exception as e:
        console.print(f"[bold red]Error:[/bold red] {e}")
        raise


def interactive_mode(provider: str = "openai"):
    console.print(Panel(
        "[bold]Salesforce Agent[/bold]\n"
        "I can query, create, update, delete, and export Salesforce data.\n"
        "Type 'quit' to exit or 'demo' to run a demo task.",
        title="[bold blue]Welcome[/bold blue]"
    ))

    agent = build_agent(provider)

    while True:
        try:
            user_input = console.input("\n[bold yellow]You:[/bold yellow] ").strip()
            if not user_input:
                continue
            if user_input.lower() in ("quit", "exit", "q"):
                console.print("[dim]Goodbye![/dim]")
                break
            if user_input.lower() == "demo":
                import random
                user_input = random.choice(DEMO_TASKS)
                console.print(f"[dim]Running demo: {user_input}[/dim]")

            result = agent.invoke({"input": user_input})
            console.print(f"\n[bold green]Agent:[/bold green] {result['output']}")

        except KeyboardInterrupt:
            console.print("\n[dim]Interrupted.[/dim]")
            break
        except Exception as e:
            console.print(f"[bold red]Error:[/bold red] {e}")


# ── Entry point ───────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Salesforce LangChain Agent")
    parser.add_argument("--task", help="Single task to run (non-interactive)")
    parser.add_argument("--provider", default=os.environ.get("LLM_PROVIDER", "openai"),
                        choices=["openai", "anthropic"], help="LLM provider")
    parser.add_argument("--demo", action="store_true", help="Run all demo tasks")
    args = parser.parse_args()

    # Connect to MCP server
    try:
        initialize_mcp()
    except Exception as e:
        console.print(f"[bold red]Failed to connect to MCP server:[/bold red] {e}")
        console.print(f"[dim]Make sure the server is running at: {os.environ.get('MCP_SERVER_URL', 'http://localhost:5000/mcp')}[/dim]")
        sys.exit(1)

    if args.demo:
        for task in DEMO_TASKS:
            run_agent(task, args.provider)
            console.print("\n" + "─" * 60 + "\n")
    elif args.task:
        run_agent(args.task, args.provider)
    else:
        interactive_mode(args.provider)


if __name__ == "__main__":
    main()
