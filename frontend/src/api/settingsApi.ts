const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5193';

async function handle<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`API ${res.status} ${res.statusText}: ${body}`);
  }
  return res.json() as Promise<T>;
}

export interface AiModelSettings {
  current: string;
  available: string[];
}

/** GET /api/settings/ai-model — which Azure OpenAI deployment the mapping pipeline calls right now. */
export async function getAiModel(): Promise<AiModelSettings> {
  const res = await fetch(`${BASE_URL}/api/settings/ai-model`);
  return handle<AiModelSettings>(res);
}

/** PUT /api/settings/ai-model — switches it; the next document processed picks up the new one. */
export async function setAiModel(deployment: string): Promise<AiModelSettings> {
  const res = await fetch(`${BASE_URL}/api/settings/ai-model`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ deployment }),
  });
  return handle<AiModelSettings>(res);
}
