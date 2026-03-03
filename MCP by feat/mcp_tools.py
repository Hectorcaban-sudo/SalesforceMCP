"""
mcp_tools.py
Shared utilities to load tools from the Salesforce MCP server and wrap them as
LangChain tools. Used by both the agent and chatbot scripts.
"""

import json
import os
import base64
from pathlib import Path
from typing import Any

import requests
from langchain.tools import StructuredTool
from pydantic import BaseModel, Field


MCP_SERVER_URL = os.environ.get("MCP_SERVER_URL", "http://localhost:5000/mcp")


# ── MCP JSON-RPC client ───────────────────────────────────────────────────────

def _mcp_call(method: str, params: dict | None = None) -> Any:
    """Send a JSON-RPC 2.0 request to the MCP server."""
    payload = {
        "jsonrpc": "2.0",
        "id": 1,
        "method": method,
        "params": params or {}
    }
    resp = requests.post(MCP_SERVER_URL, json=payload, timeout=60)
    resp.raise_for_status()
    data = resp.json()
    if "error" in data:
        raise RuntimeError(f"MCP error: {data['error']}")
    return data.get("result", {})


def initialize_mcp():
    """Perform MCP handshake."""
    result = _mcp_call("initialize", {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {"name": "python-langchain-client", "version": "1.0.0"}
    })
    print(f"✓ Connected to MCP server: {result.get('serverInfo', {})}")
    return result


def call_tool(tool_name: str, arguments: dict) -> str:
    """Call an MCP tool and return the text content."""
    result = _mcp_call("tools/call", {
        "name": tool_name,
        "arguments": arguments
    })
    # Extract text from content blocks
    content = result.get("content", [])
    texts = [block["text"] for block in content if block.get("type") == "text"]
    raw = "\n".join(texts)

    # Format nicely for the LLM
    try:
        parsed = json.loads(raw)
        return json.dumps(parsed, indent=2)
    except Exception:
        return raw


# ── Tool schemas (Pydantic) ───────────────────────────────────────────────────

class QueryInput(BaseModel):
    input: str = Field(description="Natural language query or SOQL. E.g. 'get all accounts in technology industry'")
    objectName: str | None = Field(None, description="Optional Salesforce object API name")
    limit: int | None = Field(None, description="Max records (default 50)")


class CreateInput(BaseModel):
    objectName: str = Field(description="Salesforce object API name, e.g. Account")
    input: str | None = Field(None, description="Natural language field values, e.g. 'Name=Acme, Phone=555-1234'")
    fields: dict | None = Field(None, description="Explicit field name/value pairs")


class UpdateInput(BaseModel):
    objectName: str = Field(description="Salesforce object API name")
    recordId: str = Field(description="18-character Salesforce record ID")
    input: str | None = Field(None, description="Natural language update, e.g. 'set Phone to 555-9999'")
    fields: dict | None = Field(None, description="Explicit field name/value pairs to update")


class DeleteInput(BaseModel):
    objectName: str = Field(description="Salesforce object API name")
    recordId: str = Field(description="18-character Salesforce record ID to delete")
    confirm: bool = Field(description="Must be True to confirm deletion")


class ExportInput(BaseModel):
    input: str = Field(description="Natural language export query, e.g. 'export all contacts in California'")
    objectName: str | None = Field(None, description="Optional Salesforce object API name")
    fileName: str | None = Field(None, description="Optional file name without .csv extension")


class DescribeInput(BaseModel):
    objectName: str = Field(description="Salesforce object API name or label, e.g. Account")


# ── LangChain tool builders ───────────────────────────────────────────────────

def build_langchain_tools() -> list:
    """Build all Salesforce LangChain tools backed by the MCP server."""

    def query_sf(input: str, objectName: str | None = None, limit: int | None = None) -> str:
        args = {"input": input}
        if objectName:
            args["objectName"] = objectName
        if limit:
            args["limit"] = limit
        return call_tool("salesforce_query", args)

    def create_sf(objectName: str, input: str | None = None, fields: dict | None = None) -> str:
        args: dict = {"objectName": objectName}
        if input:
            args["input"] = input
        if fields:
            args["fields"] = fields
        return call_tool("salesforce_create", args)

    def update_sf(objectName: str, recordId: str, input: str | None = None, fields: dict | None = None) -> str:
        args: dict = {"objectName": objectName, "recordId": recordId}
        if input:
            args["input"] = input
        if fields:
            args["fields"] = fields
        return call_tool("salesforce_update", args)

    def delete_sf(objectName: str, recordId: str, confirm: bool) -> str:
        return call_tool("salesforce_delete", {
            "objectName": objectName, "recordId": recordId, "confirm": confirm
        })

    def export_sf(input: str, objectName: str | None = None, fileName: str | None = None) -> str:
        """Exports Salesforce records. Returns a base64-encoded CSV and the filename."""
        args: dict = {"input": input}
        if objectName:
            args["objectName"] = objectName
        if fileName:
            args["fileName"] = fileName

        result_str = call_tool("salesforce_export_csv", args)
        try:
            result = json.loads(result_str)
            if result.get("success") and result.get("csvContent"):
                # Decode and save to disk
                csv_bytes = base64.b64decode(result["csvContent"])
                save_path = result.get("fileName", "export.csv")
                Path(save_path).write_bytes(csv_bytes)
                return f"✓ Exported {result.get('recordCount', 0)} records to '{save_path}'"
        except Exception:
            pass
        return result_str

    def describe_sf(objectName: str) -> str:
        return call_tool("salesforce_describe", {"objectName": objectName})

    def list_objects() -> str:
        return call_tool("salesforce_list_objects", {})

    return [
        StructuredTool.from_function(
            func=query_sf,
            name="salesforce_query",
            description=(
                "Query Salesforce records using natural language or SOQL. "
                "Examples: 'get all accounts', 'find contacts where email contains gmail', "
                "'show top 5 opportunities ordered by amount desc', "
                "'SELECT Id, Name FROM Lead LIMIT 10'"
            ),
            args_schema=QueryInput,
        ),
        StructuredTool.from_function(
            func=create_sf,
            name="salesforce_create",
            description=(
                "Create a new Salesforce record. Provide the object name and field values. "
                "Example: objectName='Account', input='Name=Acme Corp, Phone=555-1234, Industry=Technology'"
            ),
            args_schema=CreateInput,
        ),
        StructuredTool.from_function(
            func=update_sf,
            name="salesforce_update",
            description=(
                "Update an existing Salesforce record by its ID. "
                "Requires objectName and recordId. Provide field values via input or fields dict."
            ),
            args_schema=UpdateInput,
        ),
        StructuredTool.from_function(
            func=delete_sf,
            name="salesforce_delete",
            description=(
                "Permanently delete a Salesforce record. Requires confirm=True. "
                "CAUTION: This cannot be undone."
            ),
            args_schema=DeleteInput,
        ),
        StructuredTool.from_function(
            func=export_sf,
            name="salesforce_export_csv",
            description=(
                "Export Salesforce records to a CSV file on disk. "
                "Examples: 'export all accounts', 'export contacts in California to csv'"
            ),
            args_schema=ExportInput,
        ),
        StructuredTool.from_function(
            func=describe_sf,
            name="salesforce_describe",
            description=(
                "Describe a Salesforce object schema - shows all fields, types, and relationships. "
                "Use this to understand what fields are available before querying."
            ),
            args_schema=DescribeInput,
        ),
        StructuredTool.from_function(
            func=list_objects,
            name="salesforce_list_objects",
            description="List all Salesforce objects with loaded schemas. Use this to discover available objects.",
            args_schema=None,
        ),
    ]
