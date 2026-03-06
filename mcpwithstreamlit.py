import asyncio
import streamlit as st

from langchain_openai import ChatOpenAI
from langchain_core.messages import HumanMessage, AIMessage, ToolMessage
from langchain_core.prompts import ChatPromptTemplate, MessagesPlaceholder
from langchain_community.chat_message_histories import ChatMessageHistory

from langchain_mcp_adapters.client import MultiServerMCPClient


# -----------------------------
# Streamlit page config
# -----------------------------
st.set_page_config(page_title="Salesforce MCP Agent", layout="wide")
st.title("Salesforce MCP Agent")


# -----------------------------
# Session State
# -----------------------------
if "history" not in st.session_state:
    st.session_state.history = ChatMessageHistory()

if "tool_map" not in st.session_state:
    st.session_state.tool_map = {}

if "chain" not in st.session_state:
    st.session_state.chain = None

if "llm" not in st.session_state:
    st.session_state.llm = None


# -----------------------------
# Initialize Agent
# -----------------------------
async def init_agent():

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

    st.session_state.tool_map = {tool.name: tool for tool in tools}

    llm = ChatOpenAI(
        model="gpt-4o-mini",
        temperature=0
    ).bind_tools(tools)

    prompt = ChatPromptTemplate.from_messages([
        (
            "system",
            "You are a helpful assistant that can use tools to query or update Salesforce."
        ),
        MessagesPlaceholder("history"),
        ("human", "{input}")
    ])

    st.session_state.chain = prompt | llm
    st.session_state.llm = llm


if st.session_state.chain is None:
    asyncio.run(init_agent())


# -----------------------------
# Agent Loop
# -----------------------------
async def agent_chat(user_message):

    history = st.session_state.history

    history.add_message(HumanMessage(content=user_message))

    response = await st.session_state.chain.ainvoke({
        "input": user_message,
        "history": history.messages[-20:]
    })

    history.add_message(response)

    while response.tool_calls:

        for tool_call in response.tool_calls:

            tool_name = tool_call["name"]
            tool_args = tool_call["args"]
            tool_id = tool_call["id"]

            tool = st.session_state.tool_map[tool_name]

            result = await tool.ainvoke(tool_args)

            history.add_message(
                ToolMessage(
                    content=str(result),
                    tool_call_id=tool_id
                )
            )

        response = await st.session_state.llm.ainvoke(history.messages[-20:])
        history.add_message(response)

    return response.content


# -----------------------------
# Render Chat History
# -----------------------------
for msg in st.session_state.history.messages:

    if isinstance(msg, HumanMessage):
        with st.chat_message("user"):
            st.markdown(msg.content)

    elif isinstance(msg, AIMessage):
        with st.chat_message("assistant"):
            st.markdown(msg.content)


# -----------------------------
# Chat Input
# -----------------------------
user_input = st.chat_input("Ask something about Salesforce...")

if user_input:

    with st.chat_message("user"):
        st.markdown(user_input)

    response = asyncio.run(agent_chat(user_input))

    with st.chat_message("assistant"):
        st.markdown(response)