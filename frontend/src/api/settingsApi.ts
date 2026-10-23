import { apiFetch, handle } from './http';

export interface AiModelSettings {
  current: string;
  available: string[];
}

/** GET /api/settings/ai-model — which Azure OpenAI deployment the mapping pipeline calls right now. */
export async function getAiModel(): Promise<AiModelSettings> {
  const res = await apiFetch('/api/settings/ai-model');
  return handle<AiModelSettings>(res);
}

/** PUT /api/settings/ai-model — switches it (admins only); the next document processed picks up the new one. */
export async function setAiModel(deployment: string): Promise<AiModelSettings> {
  const res = await apiFetch('/api/settings/ai-model', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ deployment }),
  });
  return handle<AiModelSettings>(res);
}

export interface EmbeddingModelSettings {
  current: string;
  available: string[];
}

/** GET /api/settings/embedding-model — which Azure OpenAI embedding deployment is selected. */
export async function getEmbeddingModel(): Promise<EmbeddingModelSettings> {
  const res = await apiFetch('/api/settings/embedding-model');
  return handle<EmbeddingModelSettings>(res);
}

/** PUT /api/settings/embedding-model — switches it (admins only). */
export async function setEmbeddingModel(deployment: string): Promise<EmbeddingModelSettings> {
  const res = await apiFetch('/api/settings/embedding-model', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ deployment }),
  });
  return handle<EmbeddingModelSettings>(res);
}

export interface ExtractionPromptSettings {
  prompt: string;
  defaultPrompt: string;
  isCustomized: boolean;
  schema: string;
  maxLength: number;
}

/** GET /api/settings/extraction-prompt — the system prompt sent to OpenAI for every extraction (admins only). */
export async function getExtractionPrompt(): Promise<ExtractionPromptSettings> {
  const res = await apiFetch('/api/settings/extraction-prompt');
  return handle<ExtractionPromptSettings>(res);
}

/** PUT /api/settings/extraction-prompt — replaces it; the next document processed uses the new text. */
export async function setExtractionPrompt(prompt: string): Promise<ExtractionPromptSettings> {
  const res = await apiFetch('/api/settings/extraction-prompt', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ prompt }),
  });
  return handle<ExtractionPromptSettings>(res);
}

/** DELETE /api/settings/extraction-prompt — goes back to the factory prompt compiled into the app. */
export async function resetExtractionPrompt(): Promise<ExtractionPromptSettings> {
  const res = await apiFetch('/api/settings/extraction-prompt', { method: 'DELETE' });
  return handle<ExtractionPromptSettings>(res);
}
