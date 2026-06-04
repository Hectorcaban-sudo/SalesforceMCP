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
  - Record limit: global cap and/or per-object cap on how many source records to process
  - Field History migration  (e.g. AccountHistory, ContactHistory)
  - Activity History migration (Task and Event records linked to parent)
  - Dry-run mode

Requirements:
    pip install simple-salesforce requests pyyaml

History notes:
  Field History  — Salesforce does NOT allow inserting into *History objects via the API.
                   The script exports history to a JSON sidecar file so you have a full audit
                   trail. Re-inserting into a custom __History object or a reporting table
                   in an external system is the recommended pattern.

  Activity History — Task and Event records CAN be inserted. WhoId / WhatId foreign keys
                     are resolved from the source→target Id maps built during parent migration.
"""

import sys
import json
import logging
import argparse
import yaml
from copy import deepcopy
from pathlib import Path
from typing import Any
from simple_salesforce import Salesforce, SFType
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
    "LastReferencedDate", "MasterRecordId", "RecordTypeId",
}

# Fields on Task/Event that are always read-only or computed
ACTIVITY_SKIP_FIELDS = {
    "Id", "IsDeleted", "SystemModstamp", "CreatedById", "CreatedDate",
    "LastModifiedById", "LastModifiedDate", "ActivityDate",
    "IsArchived", "IsClosed", "IsVisibleInSelfService",
    "ConnectionReceivedId", "ConnectionSentId",
    # WhoId / WhatId are handled separately via FK resolution
}

# ---------------------------------------------------------------------------
# Default config template
# ---------------------------------------------------------------------------
DEFAULT_CONFIG: dict[str, Any] = {
    "source": {
        "username": "user@source.sandbox.com",
        "password": "SourcePassword",
        "security_token": "sourceToken",
        "domain": "test",
    },
    "target": {
        "username": "user@target.sandbox.com",
        "password": "TargetPassword",
        "security_token": "targetToken",
        "domain": "test",
    },
    "migration": {
        "batch_size": 200,
        "dry_run": False,
        "include_children": True,
        "record_limit": None,
        # ---- History settings ----
        "field_history": {
            "enabled": False,          # export field history for objects that opt-in
            "export_dir": "history_export",  # directory for JSON sidecar files
        },
        "activity_history": {
            "enabled": False,          # migrate Task + Event records linked to parents
            "include_tasks": True,
            "include_events": True,
            "record_limit": None,      # separate limit for activity records
        },
        "objects": [
            {
                "api_name": "Account",
                "skip_fields": ["Fax"],
                "lookup_key": "Name",
                "where_clause": "",
                "migrate_field_history": True,   # export AccountHistory
                "migrate_activity_history": True, # migrate Tasks/Events linked to Account
                "children": [
                    {
                        "api_name": "Contact",
                        "parent_field": "AccountId",
                        "lookup_key": "Email",
                        "skip_fields": [],
                        "migrate_field_history": True,
                        "migrate_activity_history": True,
                    }
                ],
            },
        ],
    },
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def connect(cfg: dict) -> Salesforce:
    log.info("Connecting to Salesforce: %s (domain=%s)", cfg["username"], cfg["domain"])
    return Salesforce(
        username=cfg["username"],
        password=cfg["password"],
        security_token=cfg["security_token"],
        domain=cfg["domain"],
    )


def get_object_fields(sf: Salesforce, obj_api_name: str) -> dict[str, dict]:
    describe = getattr(sf, obj_api_name).describe()
    return {f["name"]: f for f in describe["fields"]}


def object_exists(sf: Salesforce, obj_api_name: str) -> bool:
    """Return True if the SObject exists and is queryable in this org."""
    try:
        getattr(sf, obj_api_name).describe()
        return True
    except Exception:
        return False


def filter_fields(
    all_fields: dict[str, dict],
    selected: list[str] | None,
    skip: list[str],
) -> list[str]:
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
        if not (meta.get("createable") or meta.get("updateable")):
            continue
        result.append(name)

    if "Name" in all_fields and "Name" not in skip_set and "Name" not in result:
        result.insert(0, "Name")

    return result


def soql_query_all(sf: Salesforce, soql: str, record_limit: int | None = None) -> list[dict]:
    if record_limit is not None and " LIMIT " not in soql.upper():
        soql = f"{soql} LIMIT {record_limit}"

    result = sf.query(soql)
    records = list(result["records"])

    while not result["done"] and (record_limit is None or len(records) < record_limit):
        result = sf.query_more(result["nextRecordsUrl"], identifier_is_url=True)
        records.extend(result["records"])

    if record_limit is not None:
        records = records[:record_limit]

    for rec in records:
        rec.pop("attributes", None)
    return records


def find_target_record_id(sf_target: Salesforce, obj_api_name: str, lookup_key: str, value: Any) -> str | None:
    if value is None:
        return None
    safe_val = str(value).replace("'", "\\'")
    soql = f"SELECT Id FROM {obj_api_name} WHERE {lookup_key} = '{safe_val}' LIMIT 1"
    try:
        result = sf_target.query(soql)
        if result["totalSize"] > 0:
            return result["records"][0]["Id"]
    except Exception as exc:
        log.warning("Lookup failed for %s.%s='%s': %s", obj_api_name, lookup_key, value, exc)
    return None


# ---------------------------------------------------------------------------
# Tooling API helpers (for disabling/restoring automations)
# ---------------------------------------------------------------------------

def tooling_query(sf: Salesforce, soql: str) -> list[dict]:
    endpoint = f"{sf.base_url}tooling/query/?q={soql.replace(' ', '+')}"
    resp = sf._call_salesforce("GET", endpoint)
    return resp.json().get("records", [])


def tooling_update(sf: Salesforce, sobject_type: str, record_id: str, payload: dict):
    endpoint = f"{sf.base_url}tooling/sobjects/{sobject_type}/{record_id}"
    sf._call_salesforce("PATCH", endpoint, json=payload)


def disable_automations(sf_target: Salesforce, obj_api_name: str, dry_run: bool) -> dict:
    snapshot = {"triggers": [], "validations": [], "flows": []}

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

    validations = tooling_query(
        sf_target,
        f"SELECT Id,ValidationName,Active FROM ValidationRule WHERE EntityDefinition.QualifiedApiName='{obj_api_name}' AND Active=true"
    )
    for v in validations:
        snapshot["validations"].append({"Id": v["Id"], "Name": v["ValidationName"], "Active": v["Active"]})
        if not dry_run:
            tooling_update(sf_target, "ValidationRule", v["Id"], {"Metadata": {"active": False}})
            log.info("  Disabled validation: %s", v["ValidationName"])
        else:
            log.info("  [DRY RUN] Would disable validation: %s", v["ValidationName"])

    flow_defs = tooling_query(
        sf_target,
        f"SELECT ActiveVersionId,DeveloperName,TriggerObjectOrEventLabel FROM FlowDefinition "
        f"WHERE TriggerObjectOrEventLabel='{obj_api_name}' AND ActiveVersionId != null"
    )
    for fd in flow_defs:
        vid = fd.get("ActiveVersionId")
        if not vid:
            continue
        snapshot["flows"].append({"ActiveVersionId": vid, "DeveloperName": fd["DeveloperName"]})
        if not dry_run:
            tooling_update(sf_target, "FlowDefinition", vid, {"Metadata": {"activeVersionNumber": 0}})
            log.info("  Disabled flow: %s", fd["DeveloperName"])
        else:
            log.info("  [DRY RUN] Would disable flow: %s", fd["DeveloperName"])

    return snapshot


def restore_automations(sf_target: Salesforce, snapshot: dict, dry_run: bool):
    for t in snapshot.get("triggers", []):
        if not dry_run:
            tooling_update(sf_target, "ApexTrigger", t["Id"], {"Metadata": {"status": t["Status"]}})
            log.info("  Restored trigger: %s", t["Name"])
        else:
            log.info("  [DRY RUN] Would restore trigger: %s", t["Name"])

    for v in snapshot.get("validations", []):
        if not dry_run:
            tooling_update(sf_target, "ValidationRule", v["Id"], {"Metadata": {"active": v["Active"]}})
            log.info("  Restored validation: %s", v["Name"])
        else:
            log.info("  [DRY RUN] Would restore validation: %s", v["Name"])

    for f in snapshot.get("flows", []):
        if not dry_run:
            tooling_update(sf_target, "FlowDefinition", f["ActiveVersionId"], {"Metadata": {"activeVersionNumber": 1}})
            log.info("  Restored flow: %s", f["DeveloperName"])
        else:
            log.info("  [DRY RUN] Would restore flow: %s", f["DeveloperName"])


# ---------------------------------------------------------------------------
# Stats
# ---------------------------------------------------------------------------

class MigrationStats:
    def __init__(self):
        self.inserted = 0
        self.updated = 0
        self.skipped = 0
        self.errors = 0
        self.history_exported = 0
        self.activities_inserted = 0
        self.activities_skipped = 0
        self.activities_errors = 0

    def report(self):
        log.info(
            "Records   → Inserted: %d | Updated: %d | Skipped: %d | Errors: %d",
            self.inserted, self.updated, self.skipped, self.errors,
        )
        log.info(
            "History   → Field history rows exported: %d",
            self.history_exported,
        )
        log.info(
            "Activities→ Inserted: %d | Skipped: %d | Errors: %d",
            self.activities_inserted, self.activities_skipped, self.activities_errors,
        )


# ---------------------------------------------------------------------------
# Field History export
# ---------------------------------------------------------------------------

# Fields present on all *History objects
HISTORY_OBJECT_FIELDS = ["Id", "ParentId", "CreatedById", "CreatedDate", "Field", "OldValue", "NewValue"]

def _history_obj_name(obj_api_name: str) -> str:
    """Return the history SObject name for a given parent SObject."""
    if obj_api_name.endswith("__c"):
        # Custom object: MyObject__c → MyObject__History
        return obj_api_name[:-3] + "__History"
    # Standard object: Account → AccountHistory
    return obj_api_name + "History"


def export_field_history(
    sf_source: Salesforce,
    obj_api_name: str,
    source_ids: list[str],
    export_dir: Path,
    record_limit: int | None,
    stats: MigrationStats,
    dry_run: bool,
):
    """
    Query *History for all source_ids and write a JSON sidecar file.

    Salesforce does not allow inserting into *History objects via the API, so
    the data is exported for archival / external reporting purposes.
    The sidecar is named <export_dir>/<ObjName>History_<timestamp>.json.
    """
    hist_obj = _history_obj_name(obj_api_name)

    if not object_exists(sf_source, hist_obj):
        log.warning("  Field history object %s does not exist or is not accessible — skipping.", hist_obj)
        return

    if not source_ids:
        log.info("  No parent records to pull history for.")
        return

    log.info("  Exporting field history from %s...", hist_obj)

    # Build IN clause in chunks of 500 (SOQL limit)
    all_history: list[dict] = []
    chunk_size = 500
    for i in range(0, len(source_ids), chunk_size):
        chunk = source_ids[i: i + chunk_size]
        id_list = ", ".join(f"'{x}'" for x in chunk)
        soql = (
            f"SELECT Id, ParentId, CreatedDate, CreatedById, Field, OldValue, NewValue "
            f"FROM {hist_obj} WHERE ParentId IN ({id_list}) ORDER BY CreatedDate ASC"
        )
        rows = soql_query_all(sf_source, soql, record_limit=record_limit)
        all_history.extend(rows)

    log.info("  Found %d field history row(s) for %s.", len(all_history), obj_api_name)
    stats.history_exported += len(all_history)

    if dry_run:
        log.info("  [DRY RUN] Would export %d history rows to %s/.", len(all_history), export_dir)
        return

    export_dir.mkdir(parents=True, exist_ok=True)
    from datetime import datetime
    ts = datetime.utcnow().strftime("%Y%m%d_%H%M%S")
    out_file = export_dir / f"{hist_obj}_{ts}.json"
    with open(out_file, "w") as fh:
        json.dump(all_history, fh, indent=2, default=str)
    log.info("  Field history exported → %s", out_file)


# ---------------------------------------------------------------------------
# Activity History (Task + Event)
# ---------------------------------------------------------------------------

def _get_activity_fields(sf: Salesforce, obj_api_name: str) -> list[str]:
    """Return writable fields for Task or Event, minus system/computed fields."""
    try:
        meta = get_object_fields(sf, obj_api_name)
    except Exception as exc:
        log.warning("Could not describe %s: %s", obj_api_name, exc)
        return []

    fields = []
    for name, info in meta.items():
        if name in ACTIVITY_SKIP_FIELDS:
            continue
        if not (info.get("createable") or info.get("updateable")):
            continue
        fields.append(name)
    return fields


def migrate_activity_history(
    sf_source: Salesforce,
    sf_target: Salesforce,
    obj_api_name: str,
    source_to_target_id: dict[str, str],
    activity_cfg: dict,
    batch_size: int,
    stats: MigrationStats,
    dry_run: bool,
):
    """
    Migrate Task and/or Event records whose WhatId (or WhoId for contacts/leads)
    points to a parent record in source_to_target_id.

    WhatId  → relates to Account, Opportunity, Case, custom objects, etc.
    WhoId   → relates to Contact or Lead.

    Both are resolved to target Ids where possible.
    """
    include_tasks  = activity_cfg.get("include_tasks", True)
    include_events = activity_cfg.get("include_events", True)
    act_limit      = activity_cfg.get("record_limit")

    if not source_to_target_id:
        log.info("  No parent Id map — skipping activity history for %s.", obj_api_name)
        return

    source_ids = list(source_to_target_id.keys())
    id_list_soql = ", ".join(f"'{x}'" for x in source_ids)

    activity_types = []
    if include_tasks:
        activity_types.append("Task")
    if include_events:
        activity_types.append("Event")

    for act_type in activity_types:
        log.info("  Migrating %s records for %s...", act_type, obj_api_name)

        act_fields = _get_activity_fields(sf_source, act_type)
        if not act_fields:
            log.warning("  Could not retrieve fields for %s — skipping.", act_type)
            continue

        # Ensure WhatId and WhoId are always fetched even if not in writable list
        fetch_fields = list(set(act_fields) | {"WhatId", "WhoId"})
        field_list = ", ".join(fetch_fields)

        # ActivityHistory is a virtual object; query Task/Event directly by WhatId
        # For Contact/Lead use WhoId
        parent_fk = "WhoId" if obj_api_name in ("Contact", "Lead") else "WhatId"

        soql = (
            f"SELECT {field_list} FROM {act_type} "
            f"WHERE {parent_fk} IN ({id_list_soql}) "
            f"ORDER BY ActivityDate ASC"
        )

        records = soql_query_all(sf_source, soql, record_limit=act_limit)
        log.info("  Found %d %s record(s).", len(records), act_type)

        sf_obj: SFType = getattr(sf_target, act_type)

        for rec in records:
            rec.pop("attributes", None)
            rec.pop("Id", None)

            # Resolve WhatId → target Id
            what_id = rec.get("WhatId")
            if what_id:
                target_what = source_to_target_id.get(what_id)
                if target_what:
                    rec["WhatId"] = target_what
                else:
                    # WhatId belongs to a different object not in this migration; leave as-is
                    # or clear it to avoid cross-org Id pollution
                    log.debug("  WhatId %s not in Id map — clearing.", what_id)
                    rec.pop("WhatId", None)

            # Resolve WhoId → target Id (Contact/Lead)
            who_id = rec.get("WhoId")
            if who_id:
                target_who = source_to_target_id.get(who_id)
                if target_who:
                    rec["WhoId"] = target_who
                else:
                    log.debug("  WhoId %s not in Id map — clearing.", who_id)
                    rec.pop("WhoId", None)

            # Keep only writable fields
            clean_rec = {k: v for k, v in rec.items() if k in act_fields}

            if dry_run:
                log.debug("  [DRY RUN] Would insert %s.", act_type)
                stats.activities_inserted += 1
                continue

            try:
                result = sf_obj.create(clean_rec)
                if result.get("success"):
                    stats.activities_inserted += 1
                else:
                    log.warning("  %s insert error: %s", act_type, result.get("errors"))
                    stats.activities_errors += 1
            except SalesforceMalformedRequest as exc:
                log.error("  %s insert failed: %s", act_type, exc)
                stats.activities_errors += 1


# ---------------------------------------------------------------------------
# Core object migration
# ---------------------------------------------------------------------------

def build_upsert_record(
    source_rec: dict,
    fields: list[str],
    lookup_key: str,
    sf_target: Salesforce,
    obj_api_name: str,
) -> tuple[dict, str | None]:
    rec = {f: source_rec.get(f) for f in fields if f in source_rec}

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


def migrate_object(
    sf_source: Salesforce,
    sf_target: Salesforce,
    obj_cfg: dict,
    dry_run: bool,
    batch_size: int,
    stats: MigrationStats,
    record_limit: int | None = None,
    field_history_cfg: dict | None = None,
    activity_history_cfg: dict | None = None,
    parent_id_map: dict | None = None,
    parent_field: str | None = None,
) -> dict:
    """
    Migrate one SObject. Returns source_id → target_id map for child/history resolution.
    """
    obj_api_name   = obj_cfg["api_name"]
    lookup_key     = obj_cfg.get("lookup_key", "Name")
    where_clause   = obj_cfg.get("where_clause", "")
    selected_fields = obj_cfg.get("fields")
    skip_fields    = obj_cfg.get("skip_fields", [])
    do_field_hist  = obj_cfg.get("migrate_field_history", False)
    do_activity    = obj_cfg.get("migrate_activity_history", False)
    effective_limit: int | None = obj_cfg.get("record_limit", record_limit)

    log.info("=" * 60)
    log.info("Migrating: %s", obj_api_name)
    log.info("=" * 60)
    if effective_limit is not None:
        log.info("Record limit: %d", effective_limit)

    src_fields_meta = get_object_fields(sf_source, obj_api_name)
    fields = filter_fields(src_fields_meta, selected_fields, skip_fields)
    log.info("Fields to migrate (%d): %s", len(fields), ", ".join(fields))

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

    log.info("Disabling automations on target for %s...", obj_api_name)
    snapshot = disable_automations(sf_target, obj_api_name, dry_run)

    source_to_target_id: dict[str, str] = {}

    try:
        to_insert: list[dict] = []
        to_update: list[tuple[str, dict]] = []
        insert_src_ids: list[str] = []
        all_source_ids: list[str] = []

        for rec in source_records:
            source_id = rec.pop("Id", None)
            rec.pop("attributes", None)
            if source_id:
                all_source_ids.append(source_id)

            # Resolve parent FK for child records
            if parent_id_map and parent_field and parent_field in rec:
                source_parent_id = rec[parent_field]
                target_parent_id = parent_id_map.get(source_parent_id)
                if target_parent_id:
                    rec[parent_field] = target_parent_id
                else:
                    log.warning("  Cannot resolve parent %s=%s — skipping.", parent_field, source_parent_id)
                    stats.skipped += 1
                    continue

            clean_rec = {k: v for k, v in rec.items() if k in fields}
            _, target_id = build_upsert_record(rec, fields, lookup_key, sf_target, obj_api_name)

            if target_id:
                to_update.append((target_id, clean_rec))
                source_to_target_id[source_id] = target_id
            else:
                to_insert.append(clean_rec)
                insert_src_ids.append(source_id)

        # ---- Inserts ----
        for i in range(0, len(to_insert), batch_size):
            batch     = to_insert[i: i + batch_size]
            batch_ids = insert_src_ids[i: i + batch_size]
            log.info("  Inserting batch %d–%d of %d...", i + 1, i + len(batch), len(to_insert))
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

        # ---- Updates ----
        for i in range(0, len(to_update), batch_size):
            batch = to_update[i: i + batch_size]
            log.info("  Updating batch %d–%d of %d...", i + 1, i + len(batch), len(to_update))
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

    # ---- Field History export ----
    if do_field_hist and field_history_cfg and field_history_cfg.get("enabled"):
        log.info("Exporting field history for %s...", obj_api_name)
        export_field_history(
            sf_source=sf_source,
            obj_api_name=obj_api_name,
            source_ids=all_source_ids,
            export_dir=Path(field_history_cfg.get("export_dir", "history_export")),
            record_limit=effective_limit,
            stats=stats,
            dry_run=dry_run,
        )

    # ---- Activity History migration ----
    if do_activity and activity_history_cfg and activity_history_cfg.get("enabled"):
        log.info("Migrating activity history for %s...", obj_api_name)
        migrate_activity_history(
            sf_source=sf_source,
            sf_target=sf_target,
            obj_api_name=obj_api_name,
            source_to_target_id=source_to_target_id,
            activity_cfg=activity_history_cfg,
            batch_size=batch_size,
            stats=stats,
            dry_run=dry_run,
        )

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

    dry_run          = mig_cfg.get("dry_run", False)
    batch_size       = mig_cfg.get("batch_size", 200)
    include_children = mig_cfg.get("include_children", True)
    record_limit     = mig_cfg.get("record_limit")
    field_hist_cfg   = mig_cfg.get("field_history", {})
    activity_cfg     = mig_cfg.get("activity_history", {})
    objects          = mig_cfg.get("objects", [])

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
            field_history_cfg=field_hist_cfg,
            activity_history_cfg=activity_cfg,
        )

        if include_children and obj_cfg.get("children"):
            for child_cfg in obj_cfg["children"]:
                child_cfg_copy = deepcopy(child_cfg)
                pfield = child_cfg_copy.pop("parent_field", None)
                migrate_object(
                    sf_source, sf_target, child_cfg_copy,
                    dry_run=dry_run,
                    batch_size=batch_size,
                    stats=overall_stats,
                    record_limit=record_limit,
                    field_history_cfg=field_hist_cfg,
                    activity_history_cfg=activity_cfg,
                    parent_id_map=parent_id_map,
                    parent_field=pfield,
                )

    log.info("=" * 60)
    log.info("Migration complete.")
    overall_stats.report()


def main():
    parser = argparse.ArgumentParser(
        description="Salesforce Sandbox-to-Sandbox Data Migration Tool",
        formatter_class=argparse.RawTextHelpFormatter,
    )
    parser.add_argument("--config", "-c", default="sf_migrate_config.yaml",
                        help="Path to YAML config file")
    parser.add_argument("--generate-config", action="store_true",
                        help="Write a sample config YAML and exit")
    parser.add_argument("--dry-run", action="store_true",
                        help="Plan migration without writing to target")
    parser.add_argument("--no-children", action="store_true",
                        help="Skip child object migration")
    parser.add_argument("--limit", "-l", type=int, default=None, metavar="N",
                        help="Max records per object (overrides config record_limit)")
    parser.add_argument("--field-history", action="store_true", default=None,
                        help="Enable field history export (overrides config)")
    parser.add_argument("--no-field-history", action="store_true",
                        help="Disable field history export (overrides config)")
    parser.add_argument("--activity-history", action="store_true", default=None,
                        help="Enable activity history migration (overrides config)")
    parser.add_argument("--no-activity-history", action="store_true",
                        help="Disable activity history migration (overrides config)")
    parser.add_argument("--history-export-dir", default=None, metavar="DIR",
                        help="Directory for field history JSON exports (overrides config)")
    parser.add_argument("--log-level", default="INFO",
                        choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    args = parser.parse_args()

    logging.getLogger().setLevel(args.log_level)

    if args.generate_config:
        generate_sample_config(args.config)
        sys.exit(0)

    try:
        config = load_config(args.config)
    except FileNotFoundError:
        log.error("Config not found: %s  — run with --generate-config to create one.", args.config)
        sys.exit(1)

    mig = config["migration"]

    if args.dry_run:
        mig["dry_run"] = True
    if args.no_children:
        mig["include_children"] = False
    if args.limit is not None:
        mig["record_limit"] = args.limit
        log.info("CLI --limit: %d records per object", args.limit)
    if args.field_history:
        mig.setdefault("field_history", {})["enabled"] = True
    if args.no_field_history:
        mig.setdefault("field_history", {})["enabled"] = False
    if args.activity_history:
        mig.setdefault("activity_history", {})["enabled"] = True
    if args.no_activity_history:
        mig.setdefault("activity_history", {})["enabled"] = False
    if args.history_export_dir:
        mig.setdefault("field_history", {})["export_dir"] = args.history_export_dir

    run_migration(config)


if __name__ == "__main__":
    main()