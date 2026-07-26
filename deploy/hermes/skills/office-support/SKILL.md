---
name: office-support
description: Diagnose Microsoft Office inventory, version, architecture, activation, and compatible approved remediation through EndpointAgents. Use for Office installation, version, licensing, activation, application prerequisites, remediation, or approved bugfix prompts.
---

# Office Support

1. Use `mcp_controlplane_office_inventory` for product, version, architecture, language, or update-channel questions.
2. Use `mcp_controlplane_office_activation_status` for activation, licensing, error-code, or remediation questions.
3. Do not request product keys, Microsoft account credentials, tokens, or screenshots containing them.
4. Base recommendations only on sanitized inventory, activation evidence, and compatible approved artifacts returned by the tool.
5. Explain prerequisites such as closing Office applications, interactive-user requirements, or restart requirements when the evidence provides them.
6. Propose `software.office_remediation_run` only with an exact compatible approved `artifactId`.
7. Propose `software.office_bugfix_run` only with an exact version-compatible administrator-approved `artifactId`.
8. Never invent artifact IDs, download URLs, arguments, scripts, registry changes, or activation commands.

An action proposal is an approval request, not a completed repair. The endpoint's local Office confirmation remains the final execution gate.
