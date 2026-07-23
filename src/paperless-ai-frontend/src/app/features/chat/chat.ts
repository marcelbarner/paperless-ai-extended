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
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService, ChatAction, ChatTurn } from '../../core/services/api';

interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  actions?: ChatAction[];
}

@Component({
  selector: 'app-chat',
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatChipsModule,
    MatTooltipModule,
  ],
  templateUrl: './chat.html',
  styleUrl: './chat.scss'
})
export class ChatComponent implements AfterViewChecked {
  private readonly api = inject(ApiService);

  @ViewChild('messagesEnd') private messagesEnd!: ElementRef;

  messages = signal<ChatMessage[]>([
    {
      role: 'assistant',
      content: 'Hallo! Ich bin dein KI-Assistent für Paperless-NGX. Ich kann dir helfen, Korrespondenten umzubenennen, Beschreibungen für Tags, Dokumententypen, Speicherpfade und Custom Fields anzupassen. Was kann ich für dich tun?'
    }
  ]);

  input = signal('');
  sending = signal(false);
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
        this.messages.update(msgs => [
          ...msgs,
          { role: 'assistant', content: res.message, actions: res.actions }
        ]);
        this.sending.set(false);
        this.shouldScroll = true;
      },
      error: () => {
        this.messages.update(msgs => [
          ...msgs,
          { role: 'assistant', content: 'Fehler bei der Anfrage. Bitte versuche es erneut.' }
        ]);
        this.sending.set(false);
        this.shouldScroll = true;
      }
    });
  }

  onKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  reset() {
    this.messages.set([{
      role: 'assistant',
      content: 'Hallo! Ich bin dein KI-Assistent für Paperless-NGX. Ich kann dir helfen, Korrespondenten umzubenennen, Beschreibungen für Tags, Dokumententypen, Speicherpfade und Custom Fields anzupassen. Was kann ich für dich tun?'
    }]);
  }
}
