import asyncio

from langchain_openai import ChatOpenAI
from langchain_core.messages import HumanMessage, ToolMessage
from langchain_core.prompts import ChatPromptTemplate, MessagesPlaceholder
from langchain_community.chat_message_histories import ChatMessageHistory

from langchain_mcp_adapters.client import MultiServerMCPClient


async def main():

    # -----------------------------
    # Connect to MCP servers
    # -----------------------------
    mcp_client = MultiServerMCPClient(
        {
            "salesforce": {
                "url": "https://api.salesforce.com/platform/mcp/v1-beta.2",
                "transport": "streamable_http",
                "headers": {
                    "Authorization": "Bearer YOUR_ACCESS_TOKEN"
                }
            }
        }
    )

    tools = await mcp_client.get_tools()

    # Map tools by name for execution
    tool_map = {tool.name: tool for tool in tools}

    # -----------------------------
    # LLM
    # -----------------------------
    llm = ChatOpenAI(
        model="gpt-4o-mini",
        temperature=0
    ).bind_tools(tools)

    # -----------------------------
    # Prompt
    # -----------------------------
    prompt = ChatPromptTemplate.from_messages([
        ("system",
         "You are a helpful assistant. "
         "You can use tools to access Salesforce data when necessary."),
        MessagesPlaceholder("history"),
        ("human", "{input}")
    ])

    chain = prompt | llm

    # -----------------------------
    # Chat history
    # -----------------------------
    history = ChatMessageHistory()

    # -----------------------------
    # Chat loop
    # -----------------------------
    while True:

        user_input = input("\nUser: ")

        if user_input.lower() in ["exit", "quit"]:
            break

        history.add_message(HumanMessage(content=user_input))

        # First LLM call
        response = await chain.ainvoke({
            "input": user_input,
            "history": history.messages
        })

        history.add_message(response)

        # -----------------------------
        # TOOL EXECUTION LOOP
        # -----------------------------
        while response.tool_calls:

            for tool_call in response.tool_calls:

                tool_name = tool_call["name"]
                tool_args = tool_call["args"]
                tool_id = tool_call["id"]

                print(f"\nCalling tool: {tool_name}")

                tool = tool_map[tool_name]

                result = await tool.ainvoke(tool_args)

                history.add_message(
                    ToolMessage(
                        content=str(result),
                        tool_call_id=tool_id
                    )
                )

            response = await llm.ainvoke(history.messages)

            history.add_message(response)

        print("\nBot:", response.content)


asyncio.run(main())