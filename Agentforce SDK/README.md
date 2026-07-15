# Contract Data Quality Agent

An Agentforce agent that scans `Contract` records for missing required
fields and emails a summary to Salesforce admins.

## What's in this package

```
force-app/main/default/classes/
  FindIncompleteContractsAction.cls        # Apex invocable action: finds gaps
  NotifyAdminsOfContractGapsAction.cls      # Apex invocable action: emails admins
create_contract_agent.py                    # Defines + deploys the agent (agent-sdk)
```

## How it fits together

`agent-sdk` (https://github.com/salesforce/agent-sdk) is a **metadata
definition/deployment SDK** — it lets you describe an agent's topics,
instructions, and actions in Python, then push that definition to your org.
It does not execute SOQL or send email itself. The actual work happens in
two Apex `@InvocableMethod` classes, which the agent calls as actions:

1. **FindIncompleteContractsAction** — queries `Contract` for records
   missing key fields (Start/End Date, Contract Term, Account, billing
   address, description, etc. — edit `FIELDS_TO_CHECK` to match your org's
   definition of "complete").
2. **NotifyAdminsOfContractGapsAction** — emails that summary to all active
   `System Administrator` profile users, or to an explicit list you pass in.

## Setup

1. **Deploy the Apex classes** to your org first (the agent's actions bind
   to these by class name):
   ```bash
   sf project deploy start -d force-app -o your-org-alias
   ```

2. **Install the SDK**:
   ```bash
   pip install agent-sdk
   ```

3. **Set credentials** as environment variables:
   ```bash
   export SF_USERNAME="you@example.com"
   export SF_PASSWORD="yourPassword+securityToken"
   # or, if you already have a session:
   # export SF_SESSION_ID="..."
   # export SF_INSTANCE_URL="https://yourorg.my.salesforce.com"
   ```

4. **Generate + deploy the agent**:
   ```bash
   python create_contract_agent.py --deploy
   ```
   Omit `--deploy` to just generate the JSON definition under
   `./agent_definition/` for inspection first.

## ⚠️ Please verify before running

I don't have access to run this SDK myself, so a few specifics in
`create_contract_agent.py` are my best inference from the SDK's public
README/docs and may need small adjustments once you have it installed:

- `agent.to_json()` — confirm the actual serialization method name on the
  `Agent` model (may be `agent.dict()`, `agent.model_dump_json()`, or a
  dedicated exporter function in `agent_sdk.core`).
- `Action.apex_class` — confirm the exact field name used to bind an
  `Action` to a deployed Apex invocable class (check `agent_sdk/models/agent.py`).
- `client.create(agent)` — confirm the deployment method name/signature on
  `Agentforce` (check `agent_sdk/core/agentforce.py` or run `help(Agentforce)`
  after installing).

Run `pip show -f agent-sdk` or browse the installed package source to
confirm exact names — the repo's `docs/api_documentation.md` has the full
class reference.

## Customizing

- Change which fields count as "required" in `FIELDS_TO_CHECK` inside
  `FindIncompleteContractsAction.cls`.
- Change the admin audience by editing `getSystemAdminEmails()` (e.g. query
  a Public Group or a custom `Admin_Notification_List__c` setting instead
  of the System Administrator profile).
- Adjust the agent's tone/instructions in the `SystemMessage` and
  `topic.instructions` list in `create_contract_agent.py`.
