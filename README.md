# Agent C — OmniField Agent

Creatio package **`Cr8FieldServiceOps`**, built by team Agent C for the Creatio Agent Arena 2026 hackathon.
It backs the OmniField Agent, a multi-agent field service flow for the demo company Vantage Field Services:
voice and chat booking, technician debrief, manager approval and a CSAT follow-up.

The repository root is the package root, as exported by `clio pull-pkg`.

## Contents

| Folder | What it holds |
|---|---|
| `Schemas/` | Case extensions, the case DCM, the fee-position lookup, the manager approval process and the C# schemas (`Cr8VantageServices`, `Cr8VantageMcpActions`, approval starter/outcome, case guard listener) |
| `Data/` | Demo and configuration data bindings: lookups, SLA and calendar, system settings, MCP server and tools, knowledge base, workplace |
| `Resources/` | Localizable strings (en-US only in git) |
| `Files/` | `Cr8FieldServiceOps.csproj`, build props, `app-descriptor.json` |

All people, phone numbers and addresses in the data are fictitious demo records.

## Round-trip with the Creatio cloud trial

A cloud instance has no file-system access, so changes move through the API with clio.

**Creatio → Git**

```bash
cd <temp folder>
clio pull-pkg Cr8FieldServiceOps -e <env>
unzip -o Cr8FieldServiceOps.zip && clio extract-pkg-zip Cr8FieldServiceOps.gz
robocopy Cr8FieldServiceOps <repo> /MIR /XD .git /XF .gitignore .gitattributes README.md
cd <repo> && git status && git commit -am "<what changed>" && git push
```

**Git → Creatio**

```bash
clio push-pkg <repo> -e <env>
```

Pull from Creatio right before any push, so a newer version in Creatio is never overwritten.
