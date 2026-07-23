import {
  Component, inject, signal, ViewChild, ElementRef, AfterViewChecked
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDividerModule } from '@angular/material/divider';
import { ApiService, ChatAction, ChatTurn, PendingAction } from '../../core/services/api';

interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  completedActions?: ChatAction[];
  executedActions?: ChatAction[];   // nach Approval gefüllt
  pendingActions?: PendingAction[]; // vor Approval
  rejected?: boolean;
}

const GREETING = 'Hallo! Ich bin dein KI-Assistent für Paperless-NGX. Ich kann dir helfen:\n\n• Korrespondenten, Tags, Dokumententypen, Speicherpfade verwalten\n• Beschreibungen für alle Entitäten ohne Beschreibung generieren\n• Dokumente suchen und Metadaten batch-aktualisieren\n• Jobs überwachen und fehlgeschlagene neu starten\n• Verbindungen und Einstellungen prüfen\n\nAlle Änderungen zeige ich dir zuerst zur Genehmigung an. Was kann ich für dich tun?';

@Component({
  selector: 'app-chat',
  imports: [
    CommonModule, FormsModule,
    MatCardModule, MatButtonModule, MatIconModule,
    MatFormFieldModule, MatInputModule,
    MatProgressSpinnerModule, MatTooltipModule, MatDividerModule,
  ],
  templateUrl: './chat.html',
  styleUrl: './chat.scss'
})
export class ChatComponent implements AfterViewChecked {
  private readonly api = inject(ApiService);

  @ViewChild('messagesEnd') private messagesEnd!: ElementRef;

  messages = signal<ChatMessage[]>([{ role: 'assistant', content: GREETING }]);
  input = signal('');
  sending = signal(false);
  executing = signal(false);
  private shouldScroll = false;

  ngAfterViewChecked() {
    if (this.shouldScroll) {
      this.messagesEnd?.nativeElement?.scrollIntoView({ behavior: 'smooth' });
      this.shouldScroll = false;
    }
  }

  send() {
    const text = this.input().trim();
    if (!text || this.sending()) return;

    const history: ChatTurn[] = this.messages()
      .filter(m => m.role === 'user' || m.role === 'assistant')
      .map(m => ({ role: m.role, content: m.content }));

    this.messages.update(msgs => [...msgs, { role: 'user', content: text }]);
    this.input.set('');
    this.sending.set(true);
    this.shouldScroll = true;

    this.api.chat(history, text).subscribe({
      next: res => {
        this.messages.update(msgs => [...msgs, {
          role: 'assistant',
          content: res.message,
          completedActions: res.completedActions,
          pendingActions: res.pendingActions?.length ? res.pendingActions : undefined
        }]);
        this.sending.set(false);
        this.shouldScroll = true;
      },
      error: () => {
        this.messages.update(msgs => [...msgs, {
          role: 'assistant',
          content: 'Fehler bei der Anfrage. Bitte versuche es erneut.'
        }]);
        this.sending.set(false);
        this.shouldScroll = true;
      }
    });
  }

  approve(msgIndex: number) {
    const msg = this.messages()[msgIndex];
    if (!msg.pendingActions?.length || this.executing()) return;

    this.executing.set(true);
    const actions = msg.pendingActions;

    this.api.chatExecute(actions).subscribe({
      next: results => {
        this.messages.update(msgs => msgs.map((m, i) =>
          i === msgIndex ? { ...m, pendingActions: undefined, executedActions: results } : m
        ));
        this.executing.set(false);
        this.shouldScroll = true;
      },
      error: () => {
        this.messages.update(msgs => msgs.map((m, i) =>
          i === msgIndex ? {
            ...m, pendingActions: undefined,
            executedActions: actions.map(a => ({
              tool: a.type, description: a.description, success: false, error: 'Netzwerkfehler'
            }))
          } : m
        ));
        this.executing.set(false);
      }
    });
  }

  reject(msgIndex: number) {
    this.messages.update(msgs => msgs.map((m, i) =>
      i === msgIndex ? { ...m, pendingActions: undefined, rejected: true } : m
    ));
  }

  onKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  reset() {
    this.messages.set([{ role: 'assistant', content: GREETING }]);
  }

  actionIcon(type: string): string {
    const icons: Record<string, string> = {
      rename_entity: 'edit',
      create_entity: 'add_circle',
      delete_entity: 'delete',
      update_description: 'description',
      update_document: 'article',
      retry_jobs: 'replay',
    };
    return icons[type] ?? 'bolt';
  }
}
