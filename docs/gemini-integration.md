# Optional Gemini translation (stage 3)

**Off by default.** Nothing is sent anywhere unless the user (1) selects the "Gemini Flash" radio on the Çeviri tab, (2) accepts the consent dialog
(`GeminiConsent` in settings.json) and (3) has stored an API key. Otherwise no Gemini object, no key read and no request exist.
Other modes (Auto/Local/Google/ONNX) never call Gemini, even if a key and consent are stored.

## API (checked against ai.google.dev, 2026-11-22)
- `POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`, key in the `x-goog-api-key` header only (never URL/body/log/exception text). Redirects disabled.
- Default model `gemini-3.8-flash` (latest stable Flash; `gemini-3.6-flash` also stable). Override with `GeminiModel` in settings.json (must match `gemini-[a-z0-9.-]+`; it is placed in the URL path).
  Model ids change fast (docs list 2.5/3.x deprecations and auto-routing); `gemini-3.x` gets `thinkingConfig.thinkingLevel = "low"` (3.8 rejects `minimal`), older ids get none.
- Key: Windows DPAPI (CurrentUser, app-specific entropy) in `%APPDATA%\OyunAltyazi\gemini.key.dat`, separate from settings.json. Entered in the app (PasswordBox), cleared after save.

## What is sent
Only the subtitle line being translated (≤600 chars), up to the last 3 previous lines of the same game's pipeline (≤200 chars each), the speaker label (≤40) and,
for continuations, the Turkish already shown. No screenshots, no game/exe name, no key in the body.

## Decision: single presentation, no later "quality replacement"
The overlay shows exactly one translation per utterance. Gemini is awaited for at most **2 s**; if it fails/times out/returns invalid content the local
translation (CT2/ONNX/Google, as in Auto) is shown instead. A line is never rewritten after display (previous flicker complaints). Because Gemini is an
explicit selectable mode, users who prefer instant local output simply keep the Local mode.
Order per line: exact glossary → exact pack → Gemini cache → Gemini → local cache → local model → English fallback.
Continuations keep the existing contract (only the new part, never the shown prefix; echoed prefix stripped).

## Resilience (no background queue)
- ≤2 concurrent requests, no queue: extra callers fail fast and the local path answers.
- 429: cooldown from `Retry-After` (cap 2 min, default 30 s); 5xx/timeout/network: 15 s; 400/401/403: stop using that key until it changes.
- Cancel (pipeline replaced the line) propagates to the HTTP call; stale results are discarded by the existing sequence check.
- Cache is namespaced per provider + model + prompt version + context hash, so Gemini and local entries never mix.

## Limitations
Not exercised against the real API (no test key provided); only mocked HTTP. Gemini cost/quota are the user's. In Gemini mode a Gemini failure falls back to whatever Auto would use, which may be online Google translate when no local model is installed.
