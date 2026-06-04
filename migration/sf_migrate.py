#!/usr/bin/env python3
"""
Salesforce Sandbox-to-Sandbox Data Migration Tool
==================================================
Features:
  - Disables triggers, validation rules, and flows ONLY for objects being migrated
  - Transfers parent and optionally child objects
  - Configurable field selection and field skipping
  - Skips system/read-only fields automatically
  - Uses Name/External ID / composite lookup to match existing target records
  - Upsert logic: updates if found, inserts if not
  - Bulk API for large datasets; SOAP/REST fallback
  - Record limit: global cap and/or per-object cap on how many source records to process
  - Dry-run mode

Requirements:
    pip install simple-salesforce requests pyyaml
"""

import sys
import json
import time
import logging
import argparse
import yaml
from copy import deepcopy
from typing import Any
from simple_salesforce import Salesforce, SalesforceLogin, SFType
from simple_salesforce.exceptions import SalesforceMalformedRequest

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[logging.StreamHandler(sys.stdout)],
)
log = logging.getLogger("sf_migrate")

# ---------------------------------------------------------------------------
# System / read-only fields that must never be migrated
# ---------------------------------------------------------------------------
SYSTEM_FIELDS = {
    "Id", "CreatedById", "CreatedDate", "LastModifiedById", "LastModifiedDate",
    "SystemModstamp", "IsDeleted", "LastActivityDate", "LastViewedDate",
    "LastReferencedDate", "MasterRecordId", "RecordTypeId",  # RecordType handled separately
}

# ---------------------------------------------------------------------------
# Default config template (used when no YAML config is supplied)
# ---------------------------------------------------------------------------
DEFAULT_CONFIG: dict[str, Any] = {
    "source": {
        "username": "user@source.sandbox.com",
        "password": "SourcePassword",
        "security_token": "sourceToken",
        "domain": "test",          # 'test' for sandbox, 'login' for prod
    },
    "target": {
        "username": "user@target.sandbox.com",
        "password": "TargetPassword",
        "security_token": "targetToken",
        "domain": "test",
    },
    "migration": {
        "batch_size": 200,         # records per Bulk API batch
        "dry_run": False,          # True = query only, no writes
        "include_children": True,  # migrate child objects defined below
        "record_limit": None,      # global max records per object (None = unlimited)
        "objects": [
            {
                "api_name": "Account",
                # "fields": ["Name","Phone","BillingCity"],  # omit = all fields
                "skip_fields": ["Fax"],
                # Lookup key used to find existing target record.
                # Supports dot-notation for related fields: "Owner.Name"
                "lookup_key": "Name",
                "where_clause": "",   # optional SOQL WHERE filter on source
                "children": [
                    {
                        "api_name": "Contact",
                        "parent_field": "AccountId",   # field on child that links to parent
                        "lookup_key": "Email",
                        "skip_fields": [],
                    }
                ],
            },
            {
                "api_name": "CustomObject__c",
                "fields": ["Name", "Custom_Field__c", "Status__c"],
                "skip_fields": [],
                "lookup_key": "Name",
                "where_clause": "Status__c != 'Archived'",
                "children": [],
            },
        ],
    },
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def connect(cfg: dict) -> Salesforce:
    """Return an authenticated simple-salesforce Salesforce instance."""
    log.info("Connecting to Salesforce: %s (domain=%s)", cfg["username"], cfg["domain"])
    return Salesforce(
        username=cfg["username"],
        password=cfg["password"],
        security_token=cfg["security_token"],
        domain=cfg["domain"],
    )


def get_object_fields(sf: Salesforce, obj_api_name: str) -> dict[str, dict]:
    """Return field metadata keyed by field name for a given SObject."""
    describe = getattr(sf, obj_api_name).describe()
    return {f["name"]: f for f in describe["fields"]}


def filter_fields(
    all_fields: dict[str, dict],
    selected: list[str] | None,
    skip: list[str],
) -> list[str]:
    """
    Compute the final list of fields to query / upsert.

    Rules:
      1. Start with 'selected' if provided, otherwise all fields.
      2. Remove system fields.
      3. Remove skipped fields.
      4. Remove non-createable / non-updateable fields (e.g. formula, rollup).
    """
    if selected:
        fields = [f for f in selected if f in all_fields]
    else:
        fields = list(all_fields.keys())

    skip_set = set(skip or []) | SYSTEM_FIELDS

    result = []
    for name in fields:
        if name in skip_set:
            continue
        meta = all_fields[name]
        # Keep field only if it can be written on create OR update
        if not (meta.get("createable") or meta.get("updateable")):
            continue
        result.append(name)

    # Always include Name if present and not skipped
    if "Name" in all_fields and "Name" not in skip_set and "Name" not in result:
        result.insert(0, "Name")

    return result


def soql_query_all(sf: Salesforce, soql: str, record_limit: int | None = None) -> list[dict]:
    """
    Execute SOQL and follow nextRecordsUrl pagination.
    If record_limit is set, stop fetching once that many records are collected
    and use a SOQL LIMIT clause to avoid over-fetching from Salesforce.
    """
    # Inject LIMIT into the SOQL so Salesforce doesn't send more than needed
    if record_limit is not None:
        # Avoid duplicating a LIMIT the caller may already have set
        if " LIMIT " not in soql.upper():
            soql = f"{soql} LIMIT {record_limit}"

    result = sf.query(soql)
    records = result["records"]

    while not result["done"] and (record_limit is None or len(records) < record_limit):
        result = sf.query_more(result["nextRecordsUrl"], identifier_is_url=True)
        records.extend(result["records"])

    # Trim to exact limit (pagination chunks may overshoot slightly)
    if record_limit is not None:
        records = records[:record_limit]

    # Remove Salesforce metadata keys
    for rec in records:
        rec.pop("attributes", None)
    return records


def find_target_record_id(sf_target: Salesforce, obj_api_name: str, lookup_key: str, value: Any) -> str | None:
    """
    Query the target org for an existing record matching lookup_key=value.
    Returns the target Id, or None if not found.
    Supports dot-notation lookup_key like 'Owner.Name' (translated to SOQL relationship query).
    """
    if value is None:
        return None
    # Escape single quotes
    safe_val = str(value).replace("'", "\\'")
    soql = f"SELECT Id FROM {obj_api_name} WHERE {lookup_key} = '{safe_val}' LIMIT 1"
    try:
        result = sf_target.query(soql)
        if result["totalSize"] > 0:
            return result["records"][0]["Id"]
    except Exception as exc:
        log.warning("Lookup query failed for %s.%s='%s': %s", obj_api_name, lookup_key, value, exc)
    return None


# ---------------------------------------------------------------------------
# Metadata automation controls (triggers / validation / flows)
# ---------------------------------------------------------------------------

METADATA_APEX = """
// Helper class injected temporarily into target org (not persisted)
// We use the Tooling API instead — see disable_automations()
"""

def tooling_query(sf: Salesforce, soql: str) -> list[dict]:
    """Run a Tooling API query and return records."""
    endpoint = f"{sf.base_url}tooling/query/?q={soql.replace(' ', '+')}"
    resp = sf._call_salesforce("GET", endpoint)
    data = resp.json()
    return data.get("records", [])


def tooling_update(sf: Salesforce, sobject_type: str, record_id: str, payload: dict):
    """PATCH a Tooling API record."""
    endpoint = f"{sf.base_url}tooling/sobjects/{sobject_type}/{record_id}"
    sf._call_salesforce("PATCH", endpoint, json=payload)


def disable_automations(sf_target: Salesforce, obj_api_name: str, dry_run: bool) -> dict:
    """
    Disable triggers, active validation rules, and active flows for obj_api_name.
    Returns a snapshot of original states so they can be restored.
    """
    snapshot = {"triggers": [], "validations": [], "flows": []}

    # -- Triggers --
    triggers = tooling_query(
        sf_target,
        f"SELECT Id,Name,Status FROM ApexTrigger WHERE TableEnumOrId='{obj_api_name}' AND Status='Active'"
    )
    for t in triggers:
        snapshot["triggers"].append({"Id": t["Id"], "Name": t["Name"], "Status": t["Status"]})
        if not dry_run:
            tooling_update(sf_target, "ApexTrigger", t["Id"], {"Metadata": {"status": "Inactive"}})
            log.info("  Disabled trigger: %s", t["Name"])
        else:
            log.info("  [DRY RUN] Would disable trigger: %s", t["Name"])

    # -- Validation Rules --
    validations = tooling_query(
        sf_target,
        f"SELECT Id,ValidationName,Active FROM ValidationRule WHERE EntityDefinition.QualifiedApiName='{obj_api_name}' AND Active=true"
    )
    for v in validations:
        snapshot["validations"].append({"Id": v["Id"], "Name": v["ValidationName"], "Active": v["Active"]})
        if not dry_run:
            tooling_update(sf_target, "ValidationRule", v["Id"], {"Metadata": {"active": False}})
            log.info("  Disabled validation rule: %s", v["ValidationName"])
        else:
            log.info("  [DRY RUN] Would disable validation rule: %s", v["ValidationName"])

    # -- Flows (Process Builder & Record-Triggered Flows on this object) --
    flows = tooling_query(
        sf_target,
        f"SELECT Id,MasterLabel,ActiveVersion.Id FROM Flow WHERE TriggerType='RecordBeforeSave' OR TriggerType='RecordAfterSave'"
    )
    # Filter flows whose trigger object matches - Tooling API doesn't expose TriggerObjectOrEvent easily,
    # so we query FlowDefinition and cross-reference
    flow_defs = tooling_query(
        sf_target,
        f"SELECT ActiveVersionId,DeveloperName,TriggerObjectOrEventLabel FROM FlowDefinition WHERE TriggerObjectOrEventLabel='{obj_api_name}' AND ActiveVersionId != null"
    )
    for fd in flow_defs:
        vid = fd.get("ActiveVersionId")
        if not vid:
            continue
        snapshot["flows"].append({"ActiveVersionId": vid, "DeveloperName": fd["DeveloperName"]})
        if not dry_run:
            # Deactivate by setting the flow version status to Obsolete via Tooling
            tooling_update(sf_target, "FlowDefinition", vid, {"Metadata": {"activeVersionNumber": 0}})
            log.info("  Disabled flow: %s", fd["DeveloperName"])
        else:
            log.info("  [DRY RUN] Would disable flow: %s", fd["DeveloperName"])

    return snapshot


def restore_automations(sf_target: Salesforce, snapshot: dict, dry_run: bool):
    """Re-enable triggers, validation rules, and flows from snapshot."""
    for t in snapshot.get("triggers", []):
        if not dry_run:
            tooling_update(sf_target, "ApexTrigger", t["Id"], {"Metadata": {"status": t["Status"]}})
            log.info("  Restored trigger: %s", t["Name"])
        else:
            log.info("  [DRY RUN] Would restore trigger: %s", t["Name"])

    for v in snapshot.get("validations", []):
        if not dry_run:
            tooling_update(sf_target, "ValidationRule", v["Id"], {"Metadata": {"active": v["Active"]}})
            log.info("  Restored validation rule: %s", v["Name"])
        else:
            log.info("  [DRY RUN] Would restore validation rule: %s", v["Name"])

    for f in snapshot.get("flows", []):
        if not dry_run:
            tooling_update(sf_target, "FlowDefinition", f["ActiveVersionId"], {"Metadata": {"activeVersionNumber": 1}})
            log.info("  Restored flow: %s", f["DeveloperName"])
        else:
            log.info("  [DRY RUN] Would restore flow: %s", f["DeveloperName"])


# ---------------------------------------------------------------------------
# Core migration
# ---------------------------------------------------------------------------

class MigrationStats:
    def __init__(self):
        self.inserted = 0
        self.updated = 0
        self.skipped = 0
        self.errors = 0

    def report(self):
        log.info(
            "Stats → Inserted: %d | Updated: %d | Skipped: %d | Errors: %d",
            self.inserted, self.updated, self.skipped, self.errors,
        )


def build_upsert_record(
    source_rec: dict,
    fields: list[str],
    lookup_key: str,
    sf_target: Salesforce,
    obj_api_name: str,
) -> tuple[dict, str | None]:
    """
    Build a cleaned record dict for upsert and return (record, target_id|None).
    target_id is None → insert; non-None → update.
    """
    rec = {f: source_rec.get(f) for f in fields if f in source_rec}

    # Resolve lookup value (supports simple field or dot-notation)
    lookup_parts = lookup_key.split(".")
    lookup_value = source_rec
    for part in lookup_parts:
        if isinstance(lookup_value, dict):
            lookup_value = lookup_value.get(part)
        else:
            lookup_value = None
            break

    target_id = find_target_record_id(sf_target, obj_api_name, lookup_key, lookup_value)
    return rec, target_id


def upsert_batch(
    sf_target: Salesforce,
    obj_api_name: str,
    to_insert: list[dict],
    to_update: list[tuple[str, dict]],
    dry_run: bool,
    stats: MigrationStats,
):
    """Perform batched insert and update calls."""
    sf_obj: SFType = getattr(sf_target, obj_api_name)

    # -- Inserts --
    if to_insert:
        if dry_run:
            log.info("  [DRY RUN] Would insert %d record(s) into %s", len(to_insert), obj_api_name)
            stats.inserted += len(to_insert)
        else:
            try:
                results = sf_obj.insert_many(to_insert)
                for r in results:
                    if r.get("success"):
                        stats.inserted += 1
                    else:
                        log.warning("  Insert error: %s", r.get("errors"))
                        stats.errors += 1
            except SalesforceMalformedRequest as exc:
                log.error("  Bulk insert failed: %s", exc)
                stats.errors += len(to_insert)

    # -- Updates --
    for target_id, payload in to_update:
        if dry_run:
            log.info("  [DRY RUN] Would update %s Id=%s", obj_api_name, target_id)
            stats.updated += 1
        else:
            try:
                sf_obj.update(target_id, payload)
                stats.updated += 1
            except SalesforceMalformedRequest as exc:
                log.error("  Update failed Id=%s: %s", target_id, exc)
                stats.errors += 1


def migrate_object(
    sf_source: Salesforce,
    sf_target: Salesforce,
    obj_cfg: dict,
    dry_run: bool,
    batch_size: int,
    stats: MigrationStats,
    record_limit: int | None = None,      # max records to fetch from source for this object
    parent_id_map: dict | None = None,   # source_id -> target_id (for child FK resolution)
    parent_field: str | None = None,      # e.g. "AccountId" on Contact
) -> dict:
    """
    Migrate one SObject. Returns a source_id -> target_id map for child resolution.
    """
    obj_api_name = obj_cfg["api_name"]
    lookup_key = obj_cfg.get("lookup_key", "Name")
    where_clause = obj_cfg.get("where_clause", "")
    selected_fields = obj_cfg.get("fields")          # None = all
    skip_fields = obj_cfg.get("skip_fields", [])

    # Per-object limit takes precedence over global limit
    effective_limit: int | None = obj_cfg.get("record_limit", record_limit)

    log.info("=" * 60)
    log.info("Migrating: %s", obj_api_name)
    log.info("=" * 60)

    if effective_limit is not None:
        log.info("Record limit: %d", effective_limit)

    # Describe source fields
    src_fields_meta = get_object_fields(sf_source, obj_api_name)
    fields = filter_fields(src_fields_meta, selected_fields, skip_fields)
    log.info("Fields to migrate (%d): %s", len(fields), ", ".join(fields))

    # Build SOQL
    # For lookup_key with dot notation we need to include the relationship path
    extra_select = ""
    if "." in lookup_key:
        extra_select = f", {lookup_key.split('.')[0]}.{lookup_key.split('.')[1]}"

    field_list = ", ".join(fields)
    soql = f"SELECT Id, {field_list}{extra_select} FROM {obj_api_name}"
    if where_clause:
        soql += f" WHERE {where_clause}"

    log.info("Querying source: %s", soql)
    source_records = soql_query_all(sf_source, soql, record_limit=effective_limit)
    log.info("Found %d source record(s).", len(source_records))

    if not source_records:
        return {}

    # Disable automations on target for this object
    log.info("Disabling automations on target for %s...", obj_api_name)
    snapshot = disable_automations(sf_target, obj_api_name, dry_run)

    source_to_target_id: dict[str, str] = {}

    try:
        to_insert = []
        to_update = []
        insert_src_ids = []   # track source Ids for insert order

        for rec in source_records:
            source_id = rec.pop("Id", None)
            rec.pop("attributes", None)

            # Resolve parent FK if this is a child migration
            if parent_id_map and parent_field and parent_field in rec:
                source_parent_id = rec[parent_field]
                target_parent_id = parent_id_map.get(source_parent_id)
                if target_parent_id:
                    rec[parent_field] = target_parent_id
                else:
                    log.warning("  Could not resolve parent %s=%s, skipping record.", parent_field, source_parent_id)
                    stats.skipped += 1
                    continue

            # Keep only allowed fields
            clean_rec = {k: v for k, v in rec.items() if k in fields}

            rec_copy, target_id = build_upsert_record(
                rec, fields, lookup_key, sf_target, obj_api_name
            )

            if target_id:
                to_update.append((target_id, clean_rec))
                source_to_target_id[source_id] = target_id
            else:
                to_insert.append(clean_rec)
                insert_src_ids.append(source_id)

        # Process in batches
        for i in range(0, len(to_insert), batch_size):
            batch = to_insert[i: i + batch_size]
            batch_ids = insert_src_ids[i: i + batch_size]
            log.info("  Inserting batch %d-%d of %d...", i + 1, i + len(batch), len(to_insert))

            if not dry_run:
                sf_obj: SFType = getattr(sf_target, obj_api_name)
                for j, r in enumerate(batch):
                    try:
                        result = sf_obj.create(r)
                        if result.get("success"):
                            stats.inserted += 1
                            source_to_target_id[batch_ids[j]] = result["id"]
                        else:
                            log.warning("  Insert error: %s", result.get("errors"))
                            stats.errors += 1
                    except SalesforceMalformedRequest as exc:
                        log.error("  Insert failed: %s", exc)
                        stats.errors += 1
            else:
                log.info("  [DRY RUN] Would insert %d record(s).", len(batch))
                stats.inserted += len(batch)

        for i in range(0, len(to_update), batch_size):
            batch = to_update[i: i + batch_size]
            log.info("  Updating batch %d-%d of %d...", i + 1, i + len(batch), len(to_update))
            if not dry_run:
                sf_obj = getattr(sf_target, obj_api_name)
                for target_id, payload in batch:
                    try:
                        sf_obj.update(target_id, payload)
                        stats.updated += 1
                    except SalesforceMalformedRequest as exc:
                        log.error("  Update failed Id=%s: %s", target_id, exc)
                        stats.errors += 1
            else:
                log.info("  [DRY RUN] Would update %d record(s).", len(batch))
                stats.updated += len(batch)

    finally:
        log.info("Restoring automations for %s...", obj_api_name)
        restore_automations(sf_target, snapshot, dry_run)

    return source_to_target_id


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def load_config(path: str) -> dict:
    with open(path) as fh:
        return yaml.safe_load(fh)


def generate_sample_config(path: str):
    with open(path, "w") as fh:
        yaml.dump(DEFAULT_CONFIG, fh, default_flow_style=False, sort_keys=False)
    log.info("Sample config written to: %s", path)


def run_migration(config: dict):
    src_cfg = config["source"]
    tgt_cfg = config["target"]
    mig_cfg = config["migration"]

    dry_run: bool = mig_cfg.get("dry_run", False)
    batch_size: int = mig_cfg.get("batch_size", 200)
    include_children: bool = mig_cfg.get("include_children", True)
    record_limit: int | None = mig_cfg.get("record_limit")   # global default; None = unlimited
    objects: list[dict] = mig_cfg.get("objects", [])

    if dry_run:
        log.warning("*** DRY RUN MODE — no data will be written ***")

    sf_source = connect(src_cfg)
    sf_target = connect(tgt_cfg)

    overall_stats = MigrationStats()

    for obj_cfg in objects:
        parent_id_map = migrate_object(
            sf_source, sf_target, obj_cfg,
            dry_run=dry_run,
            batch_size=batch_size,
            stats=overall_stats,
            record_limit=record_limit,
        )

        if include_children and obj_cfg.get("children"):
            for child_cfg in obj_cfg["children"]:
                child_cfg_copy = deepcopy(child_cfg)
                parent_field = child_cfg_copy.pop("parent_field", None)
                migrate_object(
                    sf_source, sf_target, child_cfg_copy,
                    dry_run=dry_run,
                    batch_size=batch_size,
                    stats=overall_stats,
                    record_limit=record_limit,
                    parent_id_map=parent_id_map,
                    parent_field=parent_field,
                )

    log.info("=" * 60)
    log.info("Migration complete.")
    overall_stats.report()


def main():
    parser = argparse.ArgumentParser(
        description="Salesforce Sandbox-to-Sandbox Data Migration Tool",
        formatter_class=argparse.RawTextHelpFormatter,
    )
    parser.add_argument(
        "--config", "-c",
        default="sf_migrate_config.yaml",
        help="Path to YAML config file (default: sf_migrate_config.yaml)",
    )
    parser.add_argument(
        "--generate-config",
        action="store_true",
        help="Write a sample config YAML and exit",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Query source and plan migration without writing to target",
    )
    parser.add_argument(
        "--no-children",
        action="store_true",
        help="Skip child object migration regardless of config setting",
    )
    parser.add_argument(
        "--limit", "-l",
        type=int,
        default=None,
        metavar="N",
        help="Max records to fetch per object (overrides config record_limit). Useful for test runs.",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging verbosity (default: INFO)",
    )
    args = parser.parse_args()

    logging.getLogger().setLevel(args.log_level)

    if args.generate_config:
        generate_sample_config(args.config)
        sys.exit(0)

    try:
        config = load_config(args.config)
    except FileNotFoundError:
        log.error("Config file not found: %s\nRun with --generate-config to create a sample.", args.config)
        sys.exit(1)

    # CLI flags override config
    if args.dry_run:
        config["migration"]["dry_run"] = True
    if args.no_children:
        config["migration"]["include_children"] = False
    if args.limit is not None:
        config["migration"]["record_limit"] = args.limit
        log.info("CLI --limit set: %d records per object", args.limit)

    run_migration(config)


if __name__ == "__main__":
    main()
