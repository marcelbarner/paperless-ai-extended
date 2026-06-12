import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface ProcessingJob {
  id: number;
  documentId: number;
  documentTitle: string;
  jobType: 'Ocr' | 'Ai' | 'Both';
  status: 'Pending' | 'Processing' | 'Done' | 'Failed';
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

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api';

  getJobs(status?: string, page = 1, pageSize = 20): Observable<JobsPage> {
    let params = new HttpParams()
      .set('page', page)
      .set('pageSize', pageSize);
    if (status) params = params.set('status', status);
    return this.http.get<JobsPage>(`${this.base}/queue/jobs`, { params });
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

  syncMetadata(type: MetadataType): Observable<{ synced: number }> {
    return this.http.post<{ synced: number }>(`${this.base}/metadata/${type}/sync`, null);
  }

  getSettings(): Observable<Record<string, string>> {
    return this.http.get<Record<string, string>>(`${this.base}/settings`);
  }

  saveSettings(settings: Record<string, string>): Observable<void> {
    return this.http.put<void>(`${this.base}/settings`, settings);
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
}

export interface PlaygroundDocument {
  id: number;
  title: string;
  created_date: string | null;
  correspondent_id: number | null;
  document_type_id: number | null;
  has_content: boolean;
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
