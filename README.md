# Agent C — OmniField Agent

Team Agent C's entry for the Creatio Agent Arena 2026 hackathon. OmniField Agent is a multi-agent field service flow
for the demo company Vantage Field Services. It covers booking by voice and chat, the technician debrief, manager
approval and a CSAT follow-up.

The repository has two parts:

- **Part A: Creatio package.** `Cr8FieldServiceOps` sits at the repository root, exactly as `clio pull-pkg`
  exports it.
- **Part B: Web chat demo.** A customer-facing demo website that embeds the Maya web chat, to simulate the
  omnichannel customer entry point.

All people, phone numbers and addresses are fictitious demo records.

## Repository layout

```
agent-c-omnifield/
├── descriptor.json            ┐
├── Schemas/                   │
├── Data/                      │  Part A: Creatio package Cr8FieldServiceOps
├── Resources/                 │
├── Files/                     ┘
├── web-chat-demo/                Part B: web chat demo
│   ├── maya-field-service.html   customer demo page with the Maya web chat
│   └── start-demo.bat            Windows launcher, serves the page at http://localhost:8080
├── docs/
│   └── images/
│       ├── architecture.png      solution architecture
│       └── end-to-end-flow.png   end-to-end flow
└── README.md
```

`docs/` and `web-chat-demo/` are not part of the Creatio package. Keep them out of any package you build or
install (see A.3).

## Part A — Creatio package `Cr8FieldServiceOps`

### A.1 Contents

| Folder | What it holds |
|---|---|
| `Schemas/` | Case extensions, the case DCM, the fee-position lookup, the manager approval process and the C# schemas (`Cr8VantageServices`, `Cr8VantageMcpActions`, approval starter/outcome, case guard listener) |
| `Data/` | Demo and configuration data bindings: lookups, SLA and calendar, system settings, MCP server and tools, knowledge base, workplace |
| `Resources/` | Localizable strings (en-US only in git) |
| `Files/` | `Cr8FieldServiceOps.csproj`, build props, `app-descriptor.json` |

### A.2 Creatio → Git

A cloud instance has no file-system access, so changes move through the API with clio.

```bash
cd <temp folder>
clio pull-pkg Cr8FieldServiceOps -e <env>
unzip -o Cr8FieldServiceOps.zip && clio extract-pkg-zip Cr8FieldServiceOps.gz
robocopy Cr8FieldServiceOps <repo> /MIR /XD .git docs web-chat-demo /XF .gitignore .gitattributes README.md
cd <repo> && git status && git commit -am "<what changed>" && git push
```

The `/XD` and `/XF` exclusions stop `/MIR` from deleting the repository files and Part B.

### A.3 Git → Creatio

Stage the package folders only, then install:

```bash
robocopy <repo> <temp>\Cr8FieldServiceOps /E /XD .git docs web-chat-demo /XF .gitignore .gitattributes README.md
clio push-pkg <temp>\Cr8FieldServiceOps -e <env>
```

Pull from Creatio right before any push, so a newer version in Creatio is never overwritten.

## Part B — Web chat demo

`web-chat-demo/maya-field-service.html` is the Vantage Field Services demo website. It loads the Creatio omnichannel
web chat widget, which puts Maya in the bottom-right corner, so a customer can book a visit by chat.

### B.1 Run it

The widget only loads when the page is served over `http://localhost`, not when the file is opened directly.

1. **Windows:** double-click `web-chat-demo/start-demo.bat`. It opens
   `http://localhost:8080/maya-field-service.html` and serves the folder with Python or Node. If the first load
   fails, refresh.
2. **macOS / Linux:** run `python3 -m http.server 8080` in `web-chat-demo/`, then open the same URL.
3. Open the Maya chat button in the bottom-right corner once it appears. Keep the command window open during the
   demo.

### B.2 Requirements

- Python or Node.js, and internet access.
- `http://localhost:8080` in the web chat channel's allowed domains.

If the chat doesn't appear, check the allowed domains, and look for `[Vantage] Maya widget did not load` in the
browser console.
