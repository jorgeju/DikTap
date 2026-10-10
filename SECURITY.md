# Security

## Supported versions

Only the latest release receives security fixes.

## Reporting a vulnerability

**Please do not open a public issue.** Use GitHub's private report form:
[Security → Report a vulnerability](https://github.com/jorgeju/DikTap/security/advisories/new).

Include the steps to reproduce, the affected version and the impact. I will answer as soon as I can; the fix will ship in a new
release and you will be credited in the notes if you wish.

## What data DikTap handles

The OpenAI API key is stored in Windows Credential Manager; audio is sent only to `api.openai.com` and is never stored by DikTap;
there is no telemetry. Any behavior different from this is a bug worth reporting.

---

**Español:** no abras un issue público; usa el formulario privado de arriba con los pasos para reproducirlo, la versión afectada y el impacto.
