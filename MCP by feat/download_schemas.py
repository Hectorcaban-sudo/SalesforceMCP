#!/usr/bin/env python3
"""
Salesforce Schema Downloader
Downloads object schemas from Salesforce and saves them as JSON files
for use by the MCP server's SchemaRegistry.

Usage:
    python download_schemas.py --all
    python download_schemas.py --objects Account Contact Opportunity Lead
    python download_schemas.py --output ./schemas --all
"""

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Optional

try:
    from simple_salesforce import Salesforce
    from simple_salesforce.exceptions import SalesforceError
except ImportError:
    print("Installing simple_salesforce...")
    os.system("pip install simple-salesforce")
    from simple_salesforce import Salesforce

# ── Config ───────────────────────────────────────────────────────────────────

SF_USERNAME = os.environ.get("SF_USERNAME", "your_username@example.com")
SF_PASSWORD = os.environ.get("SF_PASSWORD", "your_password")
SF_SECURITY_TOKEN = os.environ.get("SF_SECURITY_TOKEN", "your_token")
SF_DOMAIN = os.environ.get("SF_DOMAIN", "login")  # "test" for sandboxes
OUTPUT_DIR = os.environ.get("SF_SCHEMA_DIR", "./schemas")

# Common standard objects to download if --all not specified
DEFAULT_OBJECTS = [
    "Account", "Contact", "Lead", "Opportunity", "Case",
    "Task", "Event", "Campaign", "Contract", "Order",
    "Product2", "Pricebook2", "PricebookEntry", "Quote",
    "User", "RecordType"
]

# ── Main ─────────────────────────────────────────────────────────────────────

def connect_to_salesforce() -> Salesforce:
    print(f"Connecting to Salesforce ({SF_DOMAIN}.salesforce.com)...")
    sf = Salesforce(
        username=SF_USERNAME,
        password=SF_PASSWORD,
        security_token=SF_SECURITY_TOKEN,
        domain=SF_DOMAIN
    )
    print(f"✓ Connected to: {sf.sf_instance}")
    return sf


def get_all_objects(sf: Salesforce) -> list[str]:
    """Retrieve all queryable custom and standard object names."""
    result = sf.describe()
    objects = [
        obj["name"] for obj in result["sobjects"]
        if obj.get("queryable") and obj.get("retrieveable")
    ]
    print(f"Found {len(objects)} queryable objects")
    return sorted(objects)


def download_schema(sf: Salesforce, object_name: str) -> Optional[dict]:
    """Download schema for a single Salesforce object."""
    try:
        obj_desc = getattr(sf, object_name).describe()

        # Build field schemas
        fields = []
        for field in obj_desc["fields"]:
            field_schema = {
                "name": field["name"],
                "label": field["label"],
                "type": field["type"],
                "length": field.get("length"),
                "nillable": field.get("nillable", True),
                "updateable": field.get("updateable", False),
                "createable": field.get("createable", False),
                "filterable": field.get("filterable", True),
                "sortable": field.get("sortable", True),
                "picklistValues": [
                    {
                        "value": pv["value"],
                        "label": pv["label"],
                        "active": pv["active"]
                    }
                    for pv in field.get("picklistValues", [])
                ]
            }
            fields.append(field_schema)

        # Build relationship schemas
        relationships = []
        for rel in obj_desc.get("fields", []):
            if rel.get("type") in ("reference", "masterdetail") and rel.get("referenceTo"):
                relationships.append({
                    "name": rel["name"],
                    "referenceTo": rel.get("referenceTo", []),
                    "relationshipName": rel.get("relationshipName")
                })

        schema = {
            "name": obj_desc["name"],
            "label": obj_desc["label"],
            "labelPlural": obj_desc["labelPlural"],
            "keyPrefix": obj_desc.get("keyPrefix"),
            "fields": fields,
            "relationships": relationships
        }

        return schema

    except SalesforceError as e:
        print(f"  ✗ Salesforce error for {object_name}: {e}")
        return None
    except Exception as e:
        print(f"  ✗ Error for {object_name}: {e}")
        return None


def save_schema(schema: dict, output_dir: str):
    """Save schema dict as a JSON file."""
    path = Path(output_dir) / f"{schema['name']}.json"
    with open(path, "w", encoding="utf-8") as f:
        json.dump(schema, f, indent=2)


def main():
    parser = argparse.ArgumentParser(
        description="Download Salesforce object schemas as JSON files"
    )
    parser.add_argument("--all", action="store_true", help="Download all queryable objects")
    parser.add_argument("--objects", nargs="+", metavar="OBJ",
                        help="Space-separated list of object API names")
    parser.add_argument("--output", default=OUTPUT_DIR, help="Output directory for JSON files")
    parser.add_argument("--username", default=SF_USERNAME)
    parser.add_argument("--password", default=SF_PASSWORD)
    parser.add_argument("--token", default=SF_SECURITY_TOKEN)
    parser.add_argument("--sandbox", action="store_true", help="Use sandbox login URL")

    args = parser.parse_args()

    # Override globals from args
    global SF_USERNAME, SF_PASSWORD, SF_SECURITY_TOKEN, SF_DOMAIN
    SF_USERNAME = args.username
    SF_PASSWORD = args.password
    SF_SECURITY_TOKEN = args.token
    SF_DOMAIN = "test" if args.sandbox else "login"

    # Ensure output dir exists
    Path(args.output).mkdir(parents=True, exist_ok=True)
    print(f"Output directory: {os.path.abspath(args.output)}")

    # Connect
    sf = connect_to_salesforce()

    # Determine objects to download
    if args.all:
        object_names = get_all_objects(sf)
    elif args.objects:
        object_names = args.objects
    else:
        print(f"No --all or --objects specified. Downloading default set: {DEFAULT_OBJECTS}")
        object_names = DEFAULT_OBJECTS

    print(f"\nDownloading {len(object_names)} object schemas...\n")

    success, failed = 0, []

    for i, obj_name in enumerate(object_names, 1):
        print(f"[{i}/{len(object_names)}] {obj_name}...", end=" ", flush=True)
        schema = download_schema(sf, obj_name)
        if schema:
            save_schema(schema, args.output)
            print(f"✓ ({len(schema['fields'])} fields)")
            success += 1
        else:
            failed.append(obj_name)

    print(f"\n{'='*50}")
    print(f"✓ Downloaded: {success} schemas")
    if failed:
        print(f"✗ Failed: {len(failed)} objects: {', '.join(failed)}")
    print(f"📁 Saved to: {os.path.abspath(args.output)}")
    print(f"\nNext: Copy the '{args.output}' directory to your MCP server's schema directory")
    print(f"      and update appsettings.json: \"SchemaDirectory\": \"{args.output}\"")


if __name__ == "__main__":
    main()
