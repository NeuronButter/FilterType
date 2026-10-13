# FilterType

A DNS App for **Technitium DNS Server** that filters configured DNS resource record types (such as `HTTPS`, `AAAA`, `SVCB`, `TXT`, etc.) for configured domain zones by returning a **NO DATA** (NODATA) response.

---

## AI Disclaimer

I needed a thing that worked and it took about 10 minutes to put this slop together since hours of headaches made me realise that Technitium DNS didn't have it built in. This is effectively just the "Filter AAAA" app but supports multiple record types and is configured per zone. I did have a better look later but take it with a grain of salt.

Main use case is because I want to keep ECH on Cloudflare (and IPv6) enabled, but HTTPS queries end up breaking local resolution and cause certificate mismatches. This is just an easy way to drop recursive requests for record types on a conditional forwarding zone.

Basically just add config like this and you're good to go.

```json
[
  {
    "name": "example.com",
    "type": "HTTPS"
  },
  {
    "name": "example.com",
    "type": "AAAA"
  }
]
```

Everything below is just token slop. 

---

## Overview

**FilterType** provides granular, per-zone record type filtering directly in Technitium DNS Server:

- **Post-processor integration** – Inspects and modifies responses in the DNS pipeline after resolution.
- **Selective record filtering** – Strips configured record types for specific domains or entire zones (including subdomains).
- **Standard NODATA responses** – Synthesizes RFC-compliant NODATA responses (`NOERROR` with empty Answer section and an `SOA` negative caching record in Authority).
- **CNAME chain awareness** – Preserves CNAME alias chains while stripping blocked record types from final targets.
- **DNSSEC awareness** – Preserves signed DNSSEC responses for DNSSEC-validating clients.

---

## Integration Details

- **Interfaces**: Implements `IDnsApplication`, `IDnsPostProcessor`
- **Hook point**: Post-processing query handler (`PostProcessAsync`)

---

## Configuration (`dnsApp.config`)

FilterType supports two configuration formats:

### 1. Simple Array Format (Recommended)

You can specify an array of zones and types to block directly:

```json
[
  {
    "name": "example.com",
    "type": "HTTPS"
  },
  {
    "name": "example.com",
    "type": "AAAA"
  }
]
```

### 2. Advanced Object Format

For more control, you can wrap the rules in a configuration object with options such as `defaultTtl`:

```json
{
  "defaultTtl": 30,
  "filters": [
    {
      "name": "example.com",
      "type": "HTTPS",
      "blockSubDomains": true
    },
    {
      "name": "example.com",
      "type": "AAAA",
      "blockSubDomains": true
    },
    {
      "name": "dualstack.internal",
      "type": "AAAA",
      "requireARecord": true
    },
    {
      "name": "corp.local",
      "types": ["HTTPS", "SVCB"]
    }
  ]
}
```

---

### Configuration Properties

#### Global Options (Object Format)

| Property     | Type    | Default | Description                                                                  |
| ------------ | ------- | ------- | ---------------------------------------------------------------------------- |
| `defaultTtl` | integer | `30`    | TTL in seconds for the `SOA` record returned in NODATA responses.            |
| `filters`    | array   | `[]`    | Array of filter rule objects. (Also accepts `blocked`, `rules`, or `zones`). |

#### Filter Rule Properties

| Property           | Type         | Default | Description                                                                                                        |
| ------------------ | ------------ | ------- | ------------------------------------------------------------------------------------------------------------------ |
| `name` (or `zone`) | string       | `""`    | The domain or zone to match. Use `"*"` or `""` for all domains.                                                    |
| `type`             | string / int | -       | The record type to filter (e.g. `"HTTPS"`, `"AAAA"`, `65`, `28`, `"ANY"`). Case-insensitive.                       |
| `types`            | array        | -       | Optional list of record types to filter for this zone (e.g. `["HTTPS", "AAAA"]`).                                  |
| `blockSubDomains`  | boolean      | `true`  | When `true`, matches the domain and all its subdomains (`*.example.com`). When `false`, only exact domain matches. |
| `requireARecord`   | boolean      | `false` | When `true`, only filters the record if an `A` record also exists for the domain (like `FilterAaaa`).              |

---

## Runtime Behavior

1. **Successful queries only**: Only acts on responses with `NoError` RCODE. `NXDOMAIN`, `SERVFAIL`, and other error responses are never modified.
2. **Zone matching**: When a rule specifies `"example.com"`, queries for `example.com`, `www.example.com`, and `api.v1.example.com` are all matched.
3. **CNAME preservation**: If `www.example.com` CNAMEs to `cdn.cloudflare.net` and `cdn.cloudflare.net` has an `HTTPS` record, the CNAME is preserved in the Answer section while the `HTTPS` record is stripped, and the Authority section receives an `SOA` record pointing to `cdn.cloudflare.net`.
4. **Multi-answer queries**: In `ANY` queries where multiple record types are returned (e.g. `A`, `AAAA`, `HTTPS`), only the configured blocked types are removed, and the remaining non-blocked records are returned to the client.

---

## Upstream Assembly Compatibility

Technitium DNS Server loads custom applications into isolated `AssemblyLoadContext` environments with assembly version verification. FilterType automatically targets and compiles against the **latest release tag** of Technitium DNS Server (`DnsServer` and `TechnitiumLibrary`).

---

## Building and Packaging

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Nushell](https://www.nushell.sh/) (`nu`)
- [mise](https://mise.jdx.dev/) (optional task runner)

---

### Using `mise` Tasks

If you use `mise`, individual tasks are defined in [`mise.toml`](file:///C:/Users/neero/Coding/FilterType/mise.toml) so you can run them in any order:

```bash
# 1. Fetch latest upstream Technitium release assemblies into lib/:
mise run fetch-tags

# 2. Run unit tests:
mise run test

# 3. Publish release binaries:
mise run build

# 4. Package published app into FilterTypeApp.zip:
mise run package

# Or run test, build, and package in sequence:
mise run all
```

---

### Scripts

Only non-trivial workflows use scripts:

- **[`scripts/fetch-tags.nu`](./scripts/fetch-tags.nu)**: Queries upstream repository tags, shallow-clones, compiles the reference assemblies, and caches them in `lib/`.
- **[`scripts/package.nu`](./scripts/package.nu)**: Archives the published app files into `artifacts/FilterTypeApp.zip` and root `FilterTypeApp.zip`.
- **[`build.nu`](./build.nu)**: Minimal 2-line helper invoking `dotnet publish` and `package.nu`.

---

### Manual Build via `dotnet` CLI

Once reference assemblies are fetched into `lib/`:

```bash
# Publish release binaries
dotnet publish -c Release

# Run unit tests
dotnet test tests/FilterType.Tests.csproj -c Release
```

> **Note:** Sideloaded app packages must include `FilterTypeApp.dll`, `FilterTypeApp.deps.json`, and `dnsApp.config`. Do **not** package `DnsServerCore.ApplicationCommon.dll` or `TechnitiumLibrary.Net.dll` into the zip, as Technitium provides these at runtime.

---

## Continuous Integration & Releases (GitHub Actions)

This repository includes automated GitHub Actions workflows:

- **`.github/workflows/ci.yml`**: Triggers on pull requests and pushes to `main`/`master`. Fetches the latest upstream Technitium release, runs the test suite, and packages `FilterTypeApp.zip`.
- **`.github/workflows/release.yml`**: Triggers on manual workflow dispatch (`workflow_dispatch`) with a required release version. Compiles and tests against the latest upstream Technitium release, packages `FilterTypeApp.zip` with the specified version, and creates a GitHub Release with the package attached.

---

## Installation in Technitium DNS Server

1. Download `FilterTypeApp.zip` from GitHub Releases.
2. Open the Technitium DNS Server Web Console (`http://<server-ip>:5380`).
3. Navigate to the **Apps** tab.
4. Click **Install App** at the top right.
5. Select and upload the downloaded `.zip` file.
6. In the Apps list, click the gear/edit icon for **FilterType** to configure your blocked zones and types in `dnsApp.config`.
7. Test query resolution via the console's **DNS Client** or using `dig`:

   ```bash
   dig @10.1.2.11 example.com HTTPS
   ```

   The query will return `NOERROR` with `0` answers and an `SOA` negative caching record in the Authority section.
