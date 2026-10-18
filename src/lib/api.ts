import { invoke, isTauri } from '@tauri-apps/api/core';

export type ImportMode = 'teacher_graded' | 'ai_grading' | 'blank';
export type ExecutionMode = 'request_permission' | 'sandbox' | 'bypass_permission';
export type Protocol = 'responses' | 'messages' | 'gemini';
export interface SourcePage {
  id: string;
  asset_id: string;
  page_index: number;
  rotation: number;
  width: number | null;
  height: number | null;
  preview_status: string;
  role: 'material' | 'reference';
}
export interface SourceDocument {
  id: string;
  name: string;
  mode: ImportMode;
  created_at: string;
  pages: SourcePage[];
  pending_pdf_pages: boolean;
}
export type Thinking =
  | { kind: 'responses' | 'adaptive'; effort: string }
  | { kind: 'budget' | 'gemini_budget'; tokens: number }
  | { kind: 'gemini_level'; level: string };
export interface ModelConfig {
  id: string;
  capabilities: {
    vision: boolean | null;
    tools: boolean | null;
    thinking: Thinking[];
    messages_budget: boolean | null;
    source: string | null;
  };
  price_input: number | null;
  price_output: number | null;
  currency: string;
}
export interface Provider {
  id: string;
  name: string;
  protocol: Protocol;
  base_url: string;
  key_ref: string | null;
  has_key: boolean;
  models: ModelConfig[];
}
export interface Settings {
  execution_mode: ExecutionMode;
  default_model: string | null;
  sandbox_networking: boolean;
}
export interface ExecutionPolicy {
  preferred_mode: ExecutionMode;
  effective_mode: ExecutionMode;
  executor_kind: 'host' | 'windows_sandbox';
  fallback_reason: string | null;
  warning: string | null;
  sandbox_networking: boolean;
}
export interface Environment {
  policy: ExecutionPolicy;
  capability: { state: string; reason: string };
  data_directory: string;
}
export interface PermissionRequest {
  digest: string;
  call: {
    tool: string;
    content: string;
    arguments: string[];
    working_directory: string;
    timeout_ms: number;
  };
}
export interface JobEvent {
  sequence: number;
  id: string;
  job_id: string;
  kind: string;
  created_at: string | number;
  payload: Job;
}
export interface Job {
  id: string;
  kind: string;
  state:
    | 'QUEUED'
    | 'RUNNING'
    | 'WAITING_PERMISSION'
    | 'COMPLETED'
    | 'FAILED'
    | 'CANCELLED'
    | 'INTERRUPTED';
  progress: number;
  message: string;
  created_at: string | number;
  updated_at: string | number;
  execution_policy: ExecutionPolicy;
  pending_permission: PermissionRequest | null;
  error: string | null;
}
export interface Bootstrap {
  documents: SourceDocument[];
  providers: Provider[];
  settings: Settings;
  environment: Environment;
  jobs: Job[];
}
export interface AssetData {
  bytes: number[];
  mime: string;
}

export const desktopAvailable = isTauri();

/** Keys are deliberately excluded from query/mutation caches and React state. */
export async function desktopCall<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  if (!desktopAvailable) throw new Error('请在 Oh My Study 桌面应用中使用此功能。');
  try {
    return await invoke<T>(command, args);
  } catch (error) {
    // Rust sends sanitized errors; do not stringify request arguments here.
    throw new Error(typeof error === 'string' ? error : '操作未完成，请查看任务详情。');
  }
}

export const api = {
  bootstrap: () => desktopCall<Bootstrap>('bootstrap'),
  selectFiles: () => desktopCall<string[]>('select_import_files'),
  listDocuments: () => desktopCall<SourceDocument[]>('list_documents'),
  importFiles: (paths: string[], name: string, mode: ImportMode, reference_paths: string[]) =>
    desktopCall<SourceDocument>('import_files', { paths, name, mode, reference_paths }),
  updatePages: (document_id: string, pages: { id: string; rotation: number }[]) =>
    desktopCall<SourceDocument>('update_pages', { document_id, pages }),
  appendReferences: (document_id: string, paths: string[]) =>
    desktopCall<SourceDocument>('append_reference_files', { document_id, paths }),
  readAsset: (asset_id: string, thumbnail = false) =>
    desktopCall<AssetData>('read_asset', { asset_id, thumbnail }),
  saveProvider: (provider: Provider, key?: string) =>
    desktopCall<Provider>('save_provider', { provider, ...(key ? { key } : {}) }),
  refreshModels: (provider_id: string) =>
    desktopCall<ModelConfig[]>('refresh_models', { provider_id }),
  saveSettings: (settings: Settings) => desktopCall<Settings>('save_settings', { settings }),
  listJobs: () => desktopCall<Job[]>('list_jobs'),
  cancelJob: (job_id: string) => desktopCall<Job>('cancel_job', { job_id }),
  resolvePermission: (
    job_id: string,
    digest: string,
    decision: 'allow_once' | 'allow_task' | 'deny',
  ) => desktopCall<Job>('resolve_permission', { job_id, digest, decision }),
  queueDiagnosticJob: (kind: 'storage' | 'permission') =>
    desktopCall<Job>('queue_diagnostic_job', { kind }),
};
