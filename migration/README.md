# Salesforce Sandbox-to-Sandbox Migration Tool

## Quick Start

```bash
# 1. Install dependencies
pip install simple-salesforce requests pyyaml

# 2. Generate a sample config (edit it before running)
python sf_migrate.py --generate-config

# 3. Dry run — reads source, plans changes, writes nothing
python sf_migrate.py --config sf_migrate_config.yaml --dry-run

# 4. Live migration
python sf_migrate.py --config sf_migrate_config.yaml
```

---

## Config Reference

### Connections
| Key | Description |
|---|---|
| `username` | Salesforce username |
| `password` | Salesforce password |
| `security_token` | Salesforce security token |
| `domain` | `test` (sandbox) or `login` (production) |

### Migration settings
| Key | Default | Description |
|---|---|---|
| `batch_size` | 200 | Records per API call |
| `dry_run` | false | Plan only, no writes |
| `record_limit` | *(none)* | Global max records per object. Per-object `record_limit` overrides this. |
| `include_children` | true | Migrate child objects defined under each object |

### Object config
| Key | Required | Description |
|---|---|---|
| `api_name` | ✅ | SObject API name (`Account`, `My_Object__c`) |
| `fields` | — | Explicit field list. Omit to use **all** writable fields |
| `skip_fields` | — | Fields to exclude even when all-fields is active |
| `lookup_key` | ✅ | Field (or dot-path like `Owner.Name`) used to match existing target records |
| `where_clause` | — | SOQL WHERE clause to filter source records |
| `record_limit` | — | Max records to pull from source for this object. Overrides the global `record_limit`. |
| `children` | — | List of child object configs (see below) |

### Child object config
Same as parent object config, plus:

| Key | Required | Description |
|---|---|---|
| `parent_field` | ✅ | FK field on the child that references the parent (`AccountId`) |

---

## How It Works

### Field selection
1. If `fields` is set → use only those fields  
2. Otherwise → use all fields from the object describe  
3. Always remove system/read-only fields (`Id`, `CreatedDate`, `SystemModstamp`, etc.)  
4. Always remove `skip_fields`  
5. Always remove non-createable and non-updateable fields (formulas, rollups, etc.)

### Record matching (upsert logic)
- Queries the **target** org using `lookup_key` and the source field value  
- **Match found** → `update` the existing record  
- **No match** → `insert` a new record  
- Parent-child FK resolution: child records have their `parent_field` swapped from the source org Id to the target org Id automatically

### Automation disabling (per-object, target org only)
Before migrating each object the tool:
1. Finds **active Apex Triggers** on that object → sets Status = Inactive  
2. Finds **active Validation Rules** on that object → sets Active = false  
3. Finds **active record-triggered Flows** for that object → deactivates them  

After migration it restores each to its original state, even if an error occurs.

---

## CLI Flags

```
--config PATH        YAML config file (default: sf_migrate_config.yaml)
--generate-config    Write a sample config and exit
--dry-run            No writes; shows what would happen
--limit N            Max records per object (overrides config record_limit). Great for test runs.
--no-children        Skip all child objects (overrides config)
--log-level LEVEL    DEBUG | INFO | WARNING | ERROR
```

---

## Notes & Tips

- **Permissions**: The target org user needs `Modify All Data` and `Customize Application` to disable automations via Tooling API.
- **External IDs**: For best matching reliability, set up an External ID field on critical objects and use it as `lookup_key`.
- **Large orgs**: For millions of records, consider using Salesforce Bulk API 2.0 directly (outside scope of this script).
- **Relationships**: Only direct parent→child relationships are resolved. Lookup fields to unrelated objects (e.g. `OwnerId` pointing to a User) are carried over as-is and may fail if target Ids differ — add those as `skip_fields` or handle manually.
- **RecordTypes**: `RecordTypeId` is excluded from migration (it's a system field). Map record types separately if needed.
