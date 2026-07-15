"""
create_contract_agent.py

Defines and deploys an Agentforce agent that:
  1. Scans Contract records for missing required data
     (via the Apex invocable action FindIncompleteContractsAction)
  2. Emails a summary to Salesforce admins
     (via the Apex invocable action NotifyAdminsOfContractGapsAction)

Prerequisites
-------------
1. pip install agent-sdk
   (repo: https://github.com/salesforce/agent-sdk)

2. Deploy the two Apex classes that ship alongside this script BEFORE
   running it, so the agent's actions can bind to real invocable methods:
     force-app/main/default/classes/FindIncompleteContractsAction.cls
     force-app/main/default/classes/NotifyAdminsOfContractGapsAction.cls

   e.g. with Salesforce CLI:
     sf project deploy start -d force-app -o your-org-alias

3. Set credentials as environment variables (recommended over hardcoding):
     SF_USERNAME, SF_PASSWORD  (password = password + security token, if required)
   or use session-based auth (SF_SESSION_ID, SF_INSTANCE_URL) instead.

Usage
-----
    python create_contract_agent.py --deploy
    python create_contract_agent.py --output-dir ./agent_def   # just generate JSON, no deploy
"""

import argparse
import os
import sys

from agent_sdk import Agentforce
from agent_sdk.core.auth import BasicAuth
from agent_sdk.models import Agent, Topic, Action, SystemMessage, Variable, Input, Output


def build_agent() -> Agent:
    """Defines the Contract Data Quality Agent: its persona, topic, and actions."""

    agent = Agent(
        name="Contract Data Quality Agent",
        description=(
            "Reviews Contract records for missing required data and notifies "
            "Salesforce admins by email so they can be corrected."
        ),
        agent_type="Internal",       # internal/employee-facing agent (not customer-facing)
        company_name="Your Company", # TODO: replace with your org's company name
    )

    agent.system_messages = [
        SystemMessage(
            message=(
                "You are a data-quality assistant for the Contracts team. "
                "Your job is to find Contract records with missing required fields "
                "and make sure admins are informed so they can fix them. "
                "Be precise, only report contracts that are genuinely missing data, "
                "and always confirm the email was sent before telling the user it's done."
            ),
            msg_type="system",
        )
    ]

    agent.variables = [
        Variable(name="onlyActivatedContracts", data_type="Boolean"),
        Variable(name="recipientEmails", data_type="Text"),
    ]

    topic = Topic(
        name="Contract Data Quality",
        description="Find contracts with missing data and notify admins.",
        scope=(
            "Handle requests to audit Contract records for missing/incomplete data "
            "and to notify Salesforce admins about what needs to be fixed."
        ),
    )

    topic.instructions = [
        "When asked to check contracts for missing data, call the "
        "'Find Contracts With Missing Data' action first.",
        "If the action reports zero incomplete contracts, tell the user everything "
        "looks complete and do NOT send an email.",
        "If incomplete contracts are found, call the 'Send Admin Notification Email' "
        "action, passing along the generated summary text and count.",
        "After sending, confirm to the user how many admins were notified and how "
        "many contracts need attention.",
        "Never fabricate contract data — only report what the action returns.",
    ]

    # --- Action 1: find contracts with missing data ---
    find_action = Action(
        name="Find Contracts With Missing Data",
        description=(
            "Scans Contract records and returns those missing key required fields "
            "(dates, term, account, billing address, description, etc.)."
        ),
        inputs=[
            Input(
                name="maxRecords",
                data_type="Number",
                description="Maximum number of contracts to scan.",
                required=False,
            ),
            Input(
                name="onlyActivated",
                data_type="Boolean",
                description="If true, only scan contracts with Status = Activated.",
                required=False,
            ),
        ],
        outputs=[
            Output(name="totalScanned", data_type="Number", description="Total contracts scanned."),
            Output(name="totalIncomplete", data_type="Number", description="Count of incomplete contracts."),
            Output(name="incompleteContracts", data_type="Text", description="List of contracts missing data."),
            Output(name="summaryText", data_type="Text", description="Human-readable summary for the email body."),
        ],
    )
    # Binds this action definition to the deployed Apex invocable method.
    find_action.apex_class = "FindIncompleteContractsAction"

    # --- Action 2: email admins ---
    notify_action = Action(
        name="Send Admin Notification Email",
        description="Emails a summary of contracts with missing data to Salesforce admins.",
        inputs=[
            Input(
                name="summaryText",
                data_type="Text",
                description="Plain-text summary of incomplete contracts to send.",
                required=True,
            ),
            Input(
                name="totalIncomplete",
                data_type="Number",
                description="Count of incomplete contracts, used in the subject line.",
                required=False,
            ),
            Input(
                name="recipientEmails",
                data_type="Text",
                description=(
                    "Optional comma-separated list of emails. If blank, all active "
                    "System Administrator profile users are notified."
                ),
                required=False,
            ),
        ],
        outputs=[
            Output(name="success", data_type="Boolean", description="Whether the email send succeeded."),
            Output(name="resultMessage", data_type="Text", description="Details about the send result."),
        ],
    )
    notify_action.apex_class = "NotifyAdminsOfContractGapsAction"

    topic.actions = [find_action, notify_action]
    agent.topics = [topic]

    return agent


def get_client() -> Agentforce:
    """Builds an Agentforce client from environment variables."""
    session_id = os.environ.get("SF_SESSION_ID")
    instance_url = os.environ.get("SF_INSTANCE_URL")
    username = os.environ.get("SF_USERNAME")
    password = os.environ.get("SF_PASSWORD")

    if session_id and instance_url:
        return Agentforce(session_id=session_id, instance_url=instance_url)

    if username and password:
        auth = BasicAuth(username=username, password=password)
        return Agentforce(auth=auth)

    raise EnvironmentError(
        "Missing credentials. Set either SF_SESSION_ID + SF_INSTANCE_URL, "
        "or SF_USERNAME + SF_PASSWORD as environment variables."
    )


def main():
    parser = argparse.ArgumentParser(description="Define and deploy the Contract Data Quality Agent.")
    parser.add_argument(
        "--output-dir", default="agent_definition",
        help="Directory to write the agent's JSON definition (always written).",
    )
    parser.add_argument(
        "--deploy", action="store_true",
        help="Also deploy the agent to Salesforce (requires credentials in env vars).",
    )
    args = parser.parse_args()

    agent = build_agent()

    os.makedirs(args.output_dir, exist_ok=True)
    definition_path = os.path.join(args.output_dir, "contract_data_quality_agent.json")
    with open(definition_path, "w") as f:
        f.write(agent.to_json(indent=2))
    print(f"Agent definition written to: {definition_path}")

    if not args.deploy:
        print("Skipping deployment (pass --deploy to push this agent to your org).")
        return

    try:
        client = get_client()
    except EnvironmentError as e:
        print(f"Cannot deploy: {e}", file=sys.stderr)
        sys.exit(1)

    print("Deploying agent to Salesforce...")
    result = client.create(agent)
    print("Deployment result:", result)


if __name__ == "__main__":
    main()
