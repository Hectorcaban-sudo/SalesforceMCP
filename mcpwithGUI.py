import asyncio
import gradio as gr

from langchain_openai import ChatOpenAI
from langchain_core.messages import HumanMessage, ToolMessage
from langchain_core.prompts import ChatPromptTemplate, MessagesPlaceholder
from langchain_community.chat_message_histories import ChatMessageHistory

from langchain_mcp_adapters.client import MultiServerMCPClient


# -----------------------------
# GLOBAL STATE
# -----------------------------
tool_map = {}
chain = None
llm = None
history = ChatMessageHistory()


# -----------------------------
# INITIALIZE AGENT
# -----------------------------
async def init_agent():

    global tool_map, chain, llm

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

    tool_map = {tool.name: tool for tool in tools}

    llm = ChatOpenAI(
        model="gpt-4o-mini",
        temperature=0
    ).bind_tools(tools)

    prompt = ChatPromptTemplate.from_messages([
        (
            "system",
            "You are a helpful assistant. "
            "You can use tools to retrieve or update Salesforce data."
        ),
        MessagesPlaceholder("history"),
        ("human", "{input}")
    ])

    chain = prompt | llm


# -----------------------------
# AGENT EXECUTION LOOP
# -----------------------------
async def agent_chat(user_message):

    history.add_message(HumanMessage(content=user_message))

    response = await chain.ainvoke({
        "input": user_message,
        "history": history.messages[-20:]   # keep last 20 messages
    })

    history.add_message(response)

    # Tool execution loop
    while response.tool_calls:

        for tool_call in response.tool_calls:

            tool_name = tool_call["name"]
            tool_args = tool_call["args"]
            tool_id = tool_call["id"]

            tool = tool_map[tool_name]

            result = await tool.ainvoke(tool_args)

            history.add_message(
                ToolMessage(
                    content=str(result),
                    tool_call_id=tool_id
                )
            )

        response = await llm.ainvoke(history.messages[-20:])

        history.add_message(response)

    return response.content


# -----------------------------
# GRADIO HANDLER
# -----------------------------
def gradio_chat(message, messages):

    response = asyncio.run(agent_chat(message))

    messages.append({
        "role": "user",
        "content": message
    })

    messages.append({
        "role": "assistant",
        "content": response
    })

    return "", messages


# -----------------------------
# START AGENT
# -----------------------------
asyncio.run(init_agent())


# -----------------------------
# GRADIO UI
# -----------------------------
with gr.Blocks() as demo:

    gr.Markdown("# Salesforce MCP Agent")

    chatbot = gr.Chatbot(
        type="messages",
        height=500
    )

    msg = gr.Textbox(
        placeholder="Ask something about Salesforce..."
    )

    msg.submit(
        gradio_chat,
        inputs=[msg, chatbot],
        outputs=[msg, chatbot]
    )

demo.launch()