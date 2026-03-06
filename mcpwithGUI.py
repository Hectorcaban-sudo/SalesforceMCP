import asyncio
import gradio as gr

from langchain_openai import ChatOpenAI
from langchain_core.messages import HumanMessage, ToolMessage
from langchain_core.prompts import ChatPromptTemplate, MessagesPlaceholder
from langchain_community.chat_message_histories import ChatMessageHistory

from langchain_mcp_adapters.client import MultiServerMCPClient


# -----------------------------
# GLOBAL OBJECTS
# -----------------------------
history = ChatMessageHistory()
tool_map = {}
chain = None
llm = None


async def init_agent():

    global tool_map, chain, llm

    mcp_client = MultiServerMCPClient(
        {
            "salesforce": {
                "url": "https://api.salesforce.com/platform/mcp/v1-beta.2",
                "transport": "streamable_http",
                "headers": {
                    "Authorization": "Bearer YOUR_TOKEN"
                }
            }
        }
    )

    tools = await mcp_client.get_tools()

    tool_map = {t.name: t for t in tools}

    llm = ChatOpenAI(
        model="gpt-4o-mini",
        temperature=0
    ).bind_tools(tools)

    prompt = ChatPromptTemplate.from_messages([
        ("system",
         "You are a helpful assistant. "
         "Use tools when necessary to retrieve Salesforce data."),
        MessagesPlaceholder("history"),
        ("human", "{input}")
    ])

    chain = prompt | llm


async def agent_chat(user_message):

    history.add_message(HumanMessage(content=user_message))

    response = await chain.ainvoke({
        "input": user_message,
        "history": history.messages
    })

    history.add_message(response)

    # -----------------------------
    # TOOL LOOP
    # -----------------------------
    while response.tool_calls:

        for call in response.tool_calls:

            tool_name = call["name"]
            tool_args = call["args"]
            tool_id = call["id"]

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

    return response.content


# -----------------------------
# GRADIO WRAPPER
# -----------------------------
def gradio_chat(message, chat_history):

    response = asyncio.run(agent_chat(message))

    chat_history.append((message, response))

    return "", chat_history


# -----------------------------
# STARTUP
# -----------------------------
asyncio.run(init_agent())


# -----------------------------
# GRADIO UI
# -----------------------------
with gr.Blocks() as demo:

    gr.Markdown("# MCP Salesforce Agent")

    chatbot = gr.Chatbot(height=500)

    msg = gr.Textbox(
        placeholder="Ask something about Salesforce..."
    )

    msg.submit(
        gradio_chat,
        inputs=[msg, chatbot],
        outputs=[msg, chatbot]
    )

demo.launch()