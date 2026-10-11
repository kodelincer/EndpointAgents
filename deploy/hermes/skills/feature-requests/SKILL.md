---
name: feature-requests
description: Handle a request this support system cannot do yet (no other EndpointAgents skill or approved repair covers it), such as installing new software, changing hardware, account or email setup, or any task outside the supported Windows, Office, IBSng and repair-package workflows. Say it is not supported yet and offer to save it for the development team.
---

# Feature Requests

Use this when the user asks for something no other installed EndpointAgents skill can do,
and no approved repair package lists a matching symptom. This is not for a supported
problem that a repair could not fix; that goes to the helpdesk through the other skills.

1. In one short sentence, say that this request is not something you can do yet. Do not
   promise that it will be added, and do not try a workaround or an unrelated tool.
2. In the same reply, ask one short question: whether they want the request saved so the
   development team can review adding it, for example
   «می‌خواهید این درخواست برای بررسی و اضافه شدن به سامانه ثبت شود؟»
3. When the user's next message clearly says yes (for example «بله»، «ثبت کن»), call
   `mcp_controlplane_record_feature_request` once with exactly
   `{ "runCapability": "<the supplied run capability>", "summary": "<one English line naming the ability>", "userRequest": "<what the user asked, in their words>" }`.
   Leave out names, passwords, phone numbers and other personal details. Then confirm in
   one short sentence that the request was saved for the team.
4. When the user says no, do not save it; reply in one short sentence that it was not
   saved.
5. Never save a request without the user's yes, and save each request only once per
   conversation. If the user also has a supported problem, handle that with its own
   skill first.

Use only the ControlPlane tool above for this workflow. Never use terminal, filesystem,
browser, web, code execution, memory, cron, delegation, learning, or skill-management
tools. Follow the profile `SOUL.md` for response language and presentation.
