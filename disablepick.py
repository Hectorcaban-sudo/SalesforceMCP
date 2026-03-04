"""
Salesforce Bulk Picklist Restriction Disabler
----------------------------------------------
Finds ALL restricted picklist fields on a given Salesforce object
and sets restricted = False on each one using simple-salesforce.

Requirements:
    pip install simple-salesforce

Usage:
    python disable_all_restricted_picklists.py \
        --username your@email.com \
        --password yourPassword \
        --token yourSecurityToken \
        --object MyObject__c

    # Sandbox:
    python disable_all_restricted_picklists.py ... --sandbox
"""

import argparse
import sys
from simple_salesforce import Salesforce


PICKLIST_TYPES = {"Picklist", "MultiselectPicklist"}


def find_and_disable_restricted_picklists(
    username: str,
    password: str,
    security_token: str,
    object_name: str,
    sandbox: bool = False,
    dry_run: bool = False,
):
    # 1. Authenticate
    print("[INFO] Authenticating ...")
    sf = Salesforce(
        username=username,
        password=password,
        security_token=security_token,
        domain="test" if sandbox else "login",
    )
    print(f"[INFO] Connected to: {sf.sf_instance}\n")

    # 2. Describe the object to get all fields
    print(f"[INFO] Describing object: {object_name}")
    try:
        obj_describe = getattr(sf, object_name).describe()
    except Exception as e:
        print(f"[ERROR] Could not describe object '{object_name}': {e}")
        sys.exit(1)

    all_fields = obj_describe.get("fields", [])
    print(f"[INFO] Total fields found: {len(all_fields)}")

    # 3. Filter to picklist fields only
    picklist_fields = [
        f["name"] for f in all_fields if f.get("type") in PICKLIST_TYPES
    ]
    print(f"[INFO] Picklist/MultiselectPicklist fields: {len(picklist_fields)}")

    if not picklist_fields:
        print("[INFO] No picklist fields found on this object. Exiting.")
        return

    # 4. Read metadata for all picklist fields in one batch
    print(f"[INFO] Reading metadata for {len(picklist_fields)} picklist field(s) ...\n")
    full_names = [f"{object_name}.{f}" for f in picklist_fields]

    # simple-salesforce mdapi.read() accepts a list
    results = sf.mdapi.CustomField.read(full_names)
    if not isinstance(results, list):
        results = [results]

    # 5. Find restricted ones
    restricted_fields = []
    for field_meta in results:
        if field_meta is None:
            continue
        restricted = getattr(field_meta, "restricted", None)
        if str(restricted).lower() == "true":
            restricted_fields.append(field_meta)

    print(f"[INFO] Restricted picklist fields found: {len(restricted_fields)}")

    if not restricted_fields:
        print("[INFO] No restricted picklist fields found. Nothing to update.")
        return

    print("\n  Fields to be updated:")
    for f in restricted_fields:
        print(f"    - {getattr(f, 'fullName', '?')}")

    if dry_run:
        print("\n[DRY RUN] No changes made. Remove --dry-run to apply updates.")
        return

    # 6. Disable restriction on each field
    print()
    success_count = 0
    fail_count = 0

    for field_meta in restricted_fields:
        name = getattr(field_meta, "fullName", "unknown")
        print(f"[INFO] Updating: {name}")
        field_meta.restricted = False

        try:
            update_result = sf.mdapi.CustomField.update(field_meta)
            if isinstance(update_result, list):
                update_result = update_result[0]

            success = getattr(update_result, "success", None)
            if str(success).lower() == "true":
                print(f"  [OK] restricted = False set successfully.")
                success_count += 1
            else:
                errors = getattr(update_result, "errors", [])
                for err in (errors if isinstance(errors, list) else [errors]):
                    msg = getattr(err, "message", str(err))
                    print(f"  [FAIL] {msg}")
                fail_count += 1

        except Exception as e:
            print(f"  [FAIL] Exception: {e}")
            fail_count += 1

    # 7. Summary
    print(f"\n{'='*50}")
    print(f"  Done. Updated: {success_count} | Failed: {fail_count}")
    print(f"{'='*50}")


def parse_args():
    parser = argparse.ArgumentParser(
        description="Find and disable all restricted picklist fields on a Salesforce object."
    )
    parser.add_argument("--username",  required=True, help="Salesforce username")
    parser.add_argument("--password",  required=True, help="Salesforce password")
    parser.add_argument("--token",     required=True, help="Salesforce security token")
    parser.add_argument("--object",    required=True, dest="object_name",
                        help="Object API name, e.g. Account or MyObject__c")
    parser.add_argument("--sandbox",   action="store_true",
                        help="Target a sandbox org")
    parser.add_argument("--dry-run",   action="store_true",
                        help="List restricted fields without making any changes")
    return parser.parse_args()


if __name__ == "__main__":
    args = parse_args()
    find_and_disable_restricted_picklists(
        username=args.username,
        password=args.password,
        security_token=args.token,
        object_name=args.object_name,
        sandbox=args.sandbox,
        dry_run=args.dry_run,
    )