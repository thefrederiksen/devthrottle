// AI settings client for the mobile Settings screen. Same-origin against the Gateway front door with
// the injected Bearer (authHeaders), exactly like the rest of client.ts. These are the SAME endpoints
// the desktop Settings "AI" tab uses - the mobile screen is just a phone-styled front end over them.
import { authHeaders, GatewayError, creditsErrorFrom } from "./client";

export type AiProviderId = "devthrottle";

/** The current AI-provider snapshot (GET/PUT /gateway/ai-provider). */
export interface AiProviderSnapshot {
  provider: AiProviderId;
  wingmanModel: string;
  wingmanFastModel: string;
  transcriptionModel: string;
  ttsModel: string;
  ttsVoice: string;
  /** Fallback voice set when the selected speech model does not advertise voices. */
  voices: string[];
  /**
   * Whether the live model CATALOG (GET /gateway/ai/models) and the Test button are available (issue #2022).
   * Gateway-owned, never guessed from the surface: false on the hosted Gateway, where the catalog and
   * test-chat routes stay denied because they spend the shared deployment credential with no per-caller
   * scoping. The AI tab reads this to disable browsing/Test and show a concise explanation
   * instead of offering a control that would fail. True on self-host.
   */
  catalogAvailable: boolean;
}

/** One model in the provider's catalog (GET /gateway/ai/models). */
export interface AiModel {
  id: string;
  description: string;
  /** For speech models: the model's own voice list (empty for chat models). */
  voices: string[];
  defaultVoice: string | null;
}

/** The result of testing a chat model (POST /gateway/ai/test-chat). */
export interface ChatTestResult {
  ok: boolean;
  reply: string;
  seconds: number;
  error: string;
}

async function getJson<T>(path: string): Promise<T> {
  const res = await fetch(path, { method: "GET", headers: { Accept: "application/json", ...authHeaders() } });
  if (!res.ok) throw new GatewayError(res.status, `GET ${path} failed: ${res.status}`);
  return (await res.json()) as T;
}

async function putJson<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(path, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const err = (await res.json().catch(() => ({}))) as { error?: string };
    throw new GatewayError(res.status, err.error ?? `PUT ${path} failed: ${res.status}`);
  }
  return (await res.json()) as T;
}

export function getAiProvider(): Promise<AiProviderSnapshot> {
  return getJson<AiProviderSnapshot>("/gateway/ai-provider");
}

export function setAiProvider(provider: AiProviderId): Promise<AiProviderSnapshot> {
  return putJson<AiProviderSnapshot>("/gateway/ai-provider", { provider });
}

// GET /gateway/ai/models?kind=chat|speech - the selected provider's live catalog. Degrades to an empty
// list on any error (not signed in / provider down) so the screen still renders the saved value.
export async function getAiModels(kind: "chat" | "speech"): Promise<AiModel[]> {
  try {
    const d = await getJson<{ models?: AiModel[] }>(`/gateway/ai/models?kind=${kind}`);
    return Array.isArray(d.models) ? d.models : [];
  } catch {
    return [];
  }
}

export async function testChat(model: string): Promise<ChatTestResult> {
  const res = await fetch("/gateway/ai/test-chat", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ model }),
  });
  const d = (await res.json().catch(() => ({}))) as Partial<ChatTestResult>;
  return { ok: Boolean(d.ok), reply: d.reply ?? "", seconds: Number(d.seconds ?? 0), error: d.error ?? (res.ok ? "" : `HTTP ${res.status}`) };
}

export function setWingmanModel(model: string): Promise<{ model: string }> {
  return putJson<{ model: string }>("/gateway/ai/wingman-model", { model });
}

export function setWingmanFastModel(model: string): Promise<{ model: string }> {
  return putJson<{ model: string }>("/gateway/ai/wingman-fast-model", { model });
}

export function setTtsModel(model: string): Promise<{ model: string }> {
  return putJson<{ model: string }>("/gateway/ai/tts-model", { model });
}

export function setTtsVoice(voice: string): Promise<{ voice: string }> {
  return putJson<{ voice: string }>("/gateway/tts-voice", { voice });
}

// POST /wingman/tts { text, model, voice } -> audio bytes to play (a short "Play sample").
export async function ttsSample(text: string, model: string, voice: string): Promise<Blob> {
  const res = await fetch("/wingman/tts", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ text, model, voice }),
  });
  if (!res.ok) {
    const err = (await res.json().catch(() => ({}))) as { error?: string };
    // "Play sample" ran into out-of-credits (issue #942): the shared notice.
    if (res.status === 402) throw creditsErrorFrom(err);
    throw new GatewayError(res.status, err.error ?? `text-to-speech failed: ${res.status}`);
  }
  return res.blob();
}
