import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface ProcessingJob {
  id: number;
  documentId: number;
  documentTitle: string;
  jobType: 'Ocr' | 'Ai' | 'Both';
  status: 'Pending' | 'Processing' | 'Done' | 'Failed' | 'Cancelled';
  createdAt: string;
  updatedAt: string;
  resultJson: string | null;
  error: string | null;
}

export interface JobsPage {
  total: number;
  page: number;
  pageSize: number;
  jobs: ProcessingJob[];
}

export interface JobStat {
  status: string;
  count: number;
}

export interface MetadataItem {
  id: number;
  entityType: string;
  entityId: number;
  name: string;
  description: string;
  updatedAt: string;
}

export type MetadataType =
  | 'correspondents'
  | 'document-types'
  | 'tags'
  | 'storage-paths'
  | 'custom-fields';

export const SYSTEM_PROMPT_KEY = 'AI:SystemPrompt';
export const USER_PROMPT_KEY = 'AI:UserPromptTemplate';

export interface AiConnection {
  id: number;
  name: string;
  endpoint: string;
  hasKey: boolean;
}

export interface AiProviderConfig {
  id: number;
  serviceType: 'OpenAI' | 'DocumentIntelligence';
  connectionId: number;
  connectionName: string;
  modelOverride: string | null;
  priority: number;
  isEnabled: boolean;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api';

  getJobs(status?: string, page?: number, pageSize?: number, jobTypes?: string[], sortBy?: string, sortDir?: string): Observable<JobsPage> {
    let params = new HttpParams();
    if (page != null) params = params.set('page', page);
    if (pageSize != null) params = params.set('pageSize', pageSize);
    if (status) params = params.set('status', status);
    for (const t of jobTypes ?? []) params = params.append('jobType', t);
    if (sortBy) params = params.set('sortBy', sortBy);
    if (sortDir) params = params.set('sortDir', sortDir);
    return this.http.get<JobsPage>(`${this.base}/queue/jobs`, { params });
  }

  batchRetry(ids: number[]): Observable<{ retried: number }> {
    return this.http.post<{ retried: number }>(`${this.base}/queue/jobs/batch-retry`, ids);
  }

  batchCancel(ids: number[]): Observable<{ cancelled: number }> {
    return this.http.post<{ cancelled: number }>(`${this.base}/queue/jobs/batch-cancel`, ids);
  }

  batchDelete(ids: number[]): Observable<{ deleted: number }> {
    return this.http.delete<{ deleted: number }>(`${this.base}/queue/jobs/batch`, { body: ids });
  }

  cancelAll(jobTypes?: string[], statuses?: string[]): Observable<{ cancelled: number }> {
    let params = new HttpParams();
    for (const t of jobTypes ?? []) params = params.append('jobType', t);
    for (const s of statuses ?? []) params = params.append('status', s);
    return this.http.post<{ cancelled: number }>(`${this.base}/queue/jobs/cancel-all`, null, { params });
  }

  deleteAll(jobTypes?: string[], statuses?: string[]): Observable<{ deleted: number }> {
    let params = new HttpParams();
    for (const t of jobTypes ?? []) params = params.append('jobType', t);
    for (const s of statuses ?? []) params = params.append('status', s);
    return this.http.delete<{ deleted: number }>(`${this.base}/queue/jobs/all`, { params });
  }

  retryAll(jobTypes?: string[]): Observable<{ retried: number }> {
    let params = new HttpParams();
    for (const t of jobTypes ?? []) params = params.append('jobType', t);
    return this.http.post<{ retried: number }>(`${this.base}/queue/jobs/retry-all`, null, { params });
  }

  getJobStats(): Observable<JobStat[]> {
    return this.http.get<JobStat[]>(`${this.base}/queue/stats`);
  }

  retryJob(id: number): Observable<ProcessingJob> {
    return this.http.post<ProcessingJob>(`${this.base}/queue/jobs/${id}/retry`, null);
  }

  getMetadata(type: MetadataType): Observable<MetadataItem[]> {
    return this.http.get<MetadataItem[]>(`${this.base}/metadata/${type}`);
  }

  updateDescription(
    type: MetadataType,
    id: number,
    name: string,
    description: string
  ): Observable<MetadataItem> {
    return this.http.put<MetadataItem>(
      `${this.base}/metadata/${type}/${id}/description`,
      { name, description }
    );
  }

  syncMetadata(type: MetadataType): Observable<{ synced: number; removed: number }> {
    return this.http.post<{ synced: number; removed: number }>(`${this.base}/metadata/${type}/sync`, null);
  }

  getSettings(): Observable<Record<string, string>> {
    return this.http.get<Record<string, string>>(`${this.base}/settings`);
  }

  saveSettings(settings: Record<string, string>): Observable<void> {
    return this.http.put<void>(`${this.base}/settings`, settings);
  }

  getConnections(): Observable<AiConnection[]> {
    return this.http.get<AiConnection[]>(`${this.base}/providers/connections`);
  }

  createConnection(name: string, endpoint: string, apiKey: string): Observable<AiConnection> {
    return this.http.post<AiConnection>(`${this.base}/providers/connections`, { name, endpoint, apiKey });
  }

  updateConnection(id: number, name: string, endpoint: string, apiKey?: string): Observable<AiConnection> {
    return this.http.put<AiConnection>(`${this.base}/providers/connections/${id}`, { name, endpoint, apiKey });
  }

  deleteConnection(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/providers/connections/${id}`);
  }

  getProviderConfigs(): Observable<AiProviderConfig[]> {
    return this.http.get<AiProviderConfig[]>(`${this.base}/providers/configs`);
  }

  createProviderConfig(config: Omit<AiProviderConfig, 'id' | 'connectionName'>): Observable<number> {
    return this.http.post<number>(`${this.base}/providers/configs`, config);
  }

  updateProviderConfig(id: number, config: Omit<AiProviderConfig, 'id' | 'connectionName'>): Observable<void> {
    return this.http.put<void>(`${this.base}/providers/configs/${id}`, config);
  }

  deleteProviderConfig(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/providers/configs/${id}`);
  }

  resetPrompt(promptKey: string): Observable<{ value: string }> {
    return this.http.post<{ value: string }>(
      `${this.base}/settings/reset-prompt/${encodeURIComponent(promptKey)}`,
      null
    );
  }

  // Playground
  playgroundDocuments(search?: string): Observable<PlaygroundDocument[]> {
    const params = search ? `?search=${encodeURIComponent(search)}` : '';
    return this.http.get<PlaygroundDocument[]>(`${this.base}/playground/documents${params}`);
  }

  playgroundPreview(documentId: number, systemPrompt: string, userPromptTemplate: string): Observable<{
    systemPrompt: string; userPrompt: string;
    charCount: { system: number; user: number; total: number };
  }> {
    return this.http.post<any>(`${this.base}/playground/preview-prompt`,
      { documentId, systemPrompt, userPromptTemplate });
  }

  playgroundRun(documentId: number, systemPrompt: string, userPromptTemplate: string): Observable<AiResult> {
    return this.http.post<AiResult>(`${this.base}/playground/run`,
      { documentId, systemPrompt, userPromptTemplate });
  }

  playgroundApply(documentId: number, result: AiResult): Observable<void> {
    return this.http.post<void>(`${this.base}/playground/apply`, {
      documentId,
      title: result.title,
      created: result.created,
      correspondentId: result.correspondentId,
      documentTypeId: result.documentTypeId,
      tagIds: result.tagIds,
      storagePathId: result.storagePathId,
      customFields: result.customFields
    });
  }

  playgroundSavePrompts(systemPrompt: string, userPromptTemplate: string): Observable<void> {
    return this.http.post<void>(`${this.base}/playground/save-prompts`,
      { systemPrompt, userPromptTemplate });
  }

  chat(history: ChatTurn[], message: string): Observable<ChatResponse> {
    return this.http.post<ChatResponse>(`${this.base}/chat/message`, { history, message });
  }

  chatExecute(actions: PendingAction[]): Observable<ChatAction[]> {
    return this.http.post<ChatAction[]>(`${this.base}/chat/execute`, { actions });
  }
}

export interface PlaygroundDocument {
  id: number;
  title: string;
  created_date: string | null;
  correspondent_id: number | null;
  document_type_id: number | null;
  has_content: boolean;
}

export interface ChatTurn {
  role: 'user' | 'assistant';
  content: string;
}

export interface ChatAction {
  tool: string;
  description: string;
  success: boolean;
  error: string | null;
}

export interface PendingAction {
  id: string;
  type: string;
  description: string;
  parameters: Record<string, unknown>;
}

export interface ChatResponse {
  message: string;
  completedActions: ChatAction[];
  pendingActions: PendingAction[];
}

export interface AiResult {
  title: string | null;
  created: string | null;
  correspondentId: number | null;
  newCorrespondent: string | null;
  documentTypeId: number | null;
  newDocumentType: string | null;
  tagIds: number[];
  newTags: string[];
  storagePathId: number | null;
  newStoragePath: string | null;
  customFields: Record<string, unknown>;
  reasoning: string | null;
  sentSystemPrompt: string | null;
  sentUserPrompt: string | null;
  toolCalls: { query: string; result: string }[];
}
