// The editor keeps the CRDT authoritative in C#. This file owns only what the DOM must own:
// caret position, key interception, and writing text back into a contenteditable without
// destroying the selection.
(function () {
  const state = {
    menuOpen: false,
    paletteOpen: false,
    shell: null,
  };

  function isField(el) {
    return el instanceof HTMLTextAreaElement || el instanceof HTMLInputElement;
  }

  function element(id) {
    const root = document.getElementById(id);
    if (!root) return null;
    if (root.isContentEditable || isField(root)) return root;
    return root.querySelector('textarea, input, [contenteditable="true"]') ?? root;
  }

  // ---------------------------------------------------------------- selection

  function caretOffset(node) {
    if (isField(node)) return node.selectionStart ?? 0;

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return 0;

    const range = selection.getRangeAt(0);
    if (!node.contains(range.startContainer)) return 0;

    const measure = range.cloneRange();
    measure.selectNodeContents(node);
    measure.setEnd(range.startContainer, range.startOffset);
    return measure.toString().length;
  }

  function selectionEnd(node) {
    if (isField(node)) return node.selectionEnd ?? 0;

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return 0;

    const range = selection.getRangeAt(0);
    if (!node.contains(range.endContainer)) return 0;

    const measure = range.cloneRange();
    measure.selectNodeContents(node);
    measure.setEnd(range.endContainer, range.endOffset);
    return measure.toString().length;
  }

  // Walks the text nodes to turn a plain character offset back into a DOM position.
  function placeCaret(node, offset) {
    if (isField(node)) {
      const n = Math.max(0, Math.min(offset, (node.value ?? '').length));
      node.setSelectionRange(n, n);
      return;
    }

    const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
    let remaining = offset;
    let target = null;

    while (walker.nextNode()) {
      const length = walker.currentNode.textContent.length;
      if (remaining <= length) {
        target = walker.currentNode;
        break;
      }
      remaining -= length;
    }

    const range = document.createRange();
    if (target) {
      range.setStart(target, Math.min(remaining, target.textContent.length));
    } else {
      range.selectNodeContents(node);
      range.collapse(false);
    }
    range.collapse(true);

    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  // Keep the code editor focused while opening the language <select>.
  document.addEventListener('mousedown', (event) => {
    const select = event.target instanceof Element
      ? event.target.closest('select.code-lang')
      : null;
    if (!select) return;
    event.preventDefault();
    if (typeof select.showPicker === 'function') {
      try {
        select.showPicker();
      } catch {
        select.focus();
      }
    } else {
      select.focus();
    }
  }, true);

  function lineInfo(node, offset) {
    const text = plainText(node);
    return {
      atStart: offset === 0,
      atEnd: offset >= text.length,
      empty: text.length === 0,
    };
  }

  function isCodeBlock(el) {
    return el.dataset.kind === 'code';
  }

  function plainText(el) {
    if (!el) return '';
    if (isField(el)) return el.value ?? '';
    return serializeContent(el);
  }

  // contenteditable's textContent concatenates sibling <div>s without newlines.
  function serializeContent(root) {
    let out = '';

    const walk = (node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        out += node.nodeValue || '';
        return;
      }
      if (node.nodeType !== Node.ELEMENT_NODE) return;

      const tag = node.tagName.toLowerCase();
      if (tag === 'br') {
        out += '\n';
        return;
      }
      if (tag === 'script' || tag === 'style') return;

      const block = /^(div|p|li|h[1-6]|blockquote|pre|tr)$/.test(tag);
      const start = out.length;
      for (const child of node.childNodes) walk(child);
      if (block && node !== root && out.length > start && !out.endsWith('\n'))
        out += '\n';
    };

    walk(root);
    return out.replace(/\u00a0/g, ' ');
  }

  function writePlain(el, text, caret) {
    if (isField(el)) {
      el.value = text;
      if (typeof caret === 'number') {
        const n = Math.max(0, Math.min(caret, text.length));
        el.setSelectionRange(n, n);
      }
      return;
    }

    el.textContent = text;
    if (typeof caret === 'number') placeCaret(el, caret);
  }

  // Empty fence, caret after a trailing newline, or Ctrl/Cmd+Enter leaves the code block.
  function shouldExitCode(text, caret, event) {
    if (event.ctrlKey || event.metaKey) return true;
    if (text.length === 0) return true;
    return caret === text.length && text.endsWith('\n');
  }

  function insertPlain(el, insertion, report) {
    const current = plainText(el);
    const caret = caretOffset(el);
    const end = selectionEnd(el);
    const from = Math.min(caret, end);
    const to = Math.max(caret, end);
    const next = current.slice(0, from) + insertion + current.slice(to);
    writePlain(el, next, from + insertion.length);
    report();
  }

  // ---------------------------------------------------------- clipboard HTML
  // GitHub (and most browsers) put a rendered fragment on text/html. We turn that
  // into the same block kinds the editor already knows, with markdown marks for
  // bold/italic/code so the preview matches what was copied.

  function tidyInline(text) {
    return (text || '').replace(/\u00a0/g, ' ').replace(/[ \t\r\n]+/g, ' ').trim();
  }

  function wrapMark(text, mark) {
    if (!text) return '';
    if (text.startsWith(mark) && text.endsWith(mark) && text.length > mark.length * 2)
      return text;
    return mark + text + mark;
  }

  function inlineText(node) {
    if (!node) return '';
    if (node.nodeType === Node.TEXT_NODE)
      return node.textContent || '';
    if (node.nodeType !== Node.ELEMENT_NODE)
      return '';

    const tag = node.tagName.toLowerCase();
    if (tag === 'br') return ' ';
    if (tag === 'script' || tag === 'style' || tag === 'meta') return '';
    if (node.classList?.contains('anchor') || node.classList?.contains('octicon'))
      return '';

    const inner = [...node.childNodes].map(inlineText).join('');
    if (tag === 'strong' || tag === 'b') return wrapMark(inner, '**');
    if (tag === 'em' || tag === 'i') return wrapMark(inner, '*');
    if (tag === 'u') return wrapMark(inner, '__');
    if (tag === 's' || tag === 'del' || tag === 'strike') return wrapMark(inner, '~~');
    if (tag === 'code' || tag === 'kbd' || tag === 'tt') return wrapMark(inner, '`');
    if (tag === 'a') return inner;
    if (tag === 'img') return node.getAttribute('alt') || '';
    return inner;
  }

  function codeLanguage(el) {
    if (!el) return '';
    const lang = el.getAttribute?.('lang') || el.getAttribute?.('data-language') || '';
    if (lang) return lang.trim();
    const cls = `${el.className || ''} ${el.getAttribute?.('class') || ''}`;
    const match = cls.match(/highlight-source-([a-z0-9+#_-]+)/i)
      || cls.match(/language-([a-z0-9+#_-]+)/i);
    return match ? match[1] : '';
  }

  function pushBlock(out, kind, text, extra) {
    const row = { kind, text: text || '' };
    if (extra?.language) row.language = extra.language;
    if (extra?.checked) row.checked = true;
    if (row.text || kind === 'Divider' || kind === 'Code')
      out.push(row);
  }

  function emitCode(el, out) {
    let text = '';
    const cells = el.querySelectorAll?.('td.blob-code');
    if (cells && cells.length) {
      text = [...cells].map((td) => td.textContent.replace(/\n$/, '')).join('\n');
    } else {
      const pre = el.tagName === 'PRE' ? el : (el.querySelector?.('pre') || el);
      text = (pre.textContent || '').replace(/\n$/, '');
    }
    const language = codeLanguage(el) || codeLanguage(el.querySelector?.('pre, code'));
    pushBlock(out, 'Code', text, { language });
  }

  function isCodeHost(el) {
    if (!el.classList) return false;
    return el.classList.contains('highlight')
      || el.classList.contains('snippet-clipboard-content')
      || [...el.classList].some((name) => name.startsWith('highlight-source'));
  }

  function liKind(li) {
    const box = li.querySelector?.(':scope > input[type=checkbox], :scope > p > input[type=checkbox]');
    if (box || li.classList?.contains('task-list-item'))
      return { kind: 'Todo', checked: !!(box && box.checked) };
    if (li.parentElement && li.parentElement.tagName === 'OL')
      return { kind: 'Numbered' };
    return { kind: 'Bullet' };
  }

  function liText(li) {
    let text = '';
    for (const child of li.childNodes) {
      if (child.nodeType === Node.ELEMENT_NODE && /^(UL|OL)$/i.test(child.tagName))
        continue;
      if (child.nodeType === Node.ELEMENT_NODE && child.tagName === 'INPUT')
        continue;
      text += inlineText(child);
    }
    return tidyInline(text);
  }

  function hasBlockChild(el) {
    for (const child of el.children || []) {
      if (/^(P|H[1-6]|UL|OL|LI|PRE|BLOCKQUOTE|TABLE|HR|DIV|SECTION|ARTICLE|TR)$/i.test(child.tagName))
        return true;
    }
    return false;
  }

  function walkBlocks(node, out) {
    if (!node) return;
    let inline = '';
    const flushInline = () => {
      const text = tidyInline(inline);
      if (text) pushBlock(out, 'Paragraph', text);
      inline = '';
    };

    for (const child of node.childNodes) {
      if (child.nodeType === Node.TEXT_NODE) {
        inline += child.textContent || '';
        continue;
      }
      if (child.nodeType !== Node.ELEMENT_NODE) continue;

      const tag = child.tagName.toLowerCase();
      if (tag === 'script' || tag === 'style' || tag === 'meta' || tag === 'button')
        continue;
      if (child.classList?.contains('anchor')) continue;

      if (tag === 'br') {
        inline += ' ';
        continue;
      }

      if (tag === 'h1') { flushInline(); pushBlock(out, 'Heading1', tidyInline(inlineText(child))); continue; }
      if (tag === 'h2') { flushInline(); pushBlock(out, 'Heading2', tidyInline(inlineText(child))); continue; }
      if (tag === 'h3') { flushInline(); pushBlock(out, 'Heading3', tidyInline(inlineText(child))); continue; }
      if (tag === 'h4' || tag === 'h5' || tag === 'h6') {
        flushInline();
        pushBlock(out, 'Heading3', tidyInline(inlineText(child)));
        continue;
      }
      if (tag === 'p') { flushInline(); pushBlock(out, 'Paragraph', tidyInline(inlineText(child))); continue; }
      if (tag === 'hr') { flushInline(); pushBlock(out, 'Divider', ''); continue; }
      if (tag === 'pre' || (tag === 'div' && isCodeHost(child))
          || (tag === 'table' && child.classList?.contains('highlight'))) {
        flushInline();
        emitCode(child, out);
        continue;
      }
      if (tag === 'blockquote') {
        flushInline();
        const before = out.length;
        walkBlocks(child, out);
        if (out.length === before) {
          pushBlock(out, 'Quote', tidyInline(inlineText(child)));
        } else {
          for (let i = before; i < out.length; i++) {
            if (out[i].kind === 'Paragraph') out[i].kind = 'Quote';
          }
        }
        continue;
      }
      if (tag === 'ul' || tag === 'ol') {
        flushInline();
        for (const li of child.children) {
          if (li.tagName !== 'LI') continue;
          const item = liKind(li);
          pushBlock(out, item.kind, liText(li), { checked: item.checked });
          for (const nested of li.children) {
            if (/^(UL|OL)$/i.test(nested.tagName)) walkBlocks(nested, out);
          }
        }
        continue;
      }
      if (tag === 'table') {
        flushInline();
        const rows = [...child.querySelectorAll('tr')].map((tr) =>
          [...tr.children].map((cell) => tidyInline(inlineText(cell))).join(' | '));
        pushBlock(out, 'Code', rows.join('\n'));
        continue;
      }
      if (tag === 'li') {
        flushInline();
        const item = liKind(child);
        pushBlock(out, item.kind, liText(child), { checked: item.checked });
        continue;
      }

      if (hasBlockChild(child)) {
        flushInline();
        walkBlocks(child, out);
        continue;
      }

      inline += inlineText(child);
    }

    flushInline();
  }

  function htmlToBlocks(html) {
    if (!html || !html.trim()) return [];
    const doc = new DOMParser().parseFromString(html, 'text/html');
    doc.querySelectorAll('a.anchor, .octicon, button, script, style, meta').forEach((n) => n.remove());
    const out = [];
    walkBlocks(doc.body, out);
    return out;
  }

  function hasInlineMarks(text) {
    return /(?:\*\*|__|~~|`|\*[^*\s])/.test(text || '');
  }

  function shouldPasteAsBlocks(blocks, plain) {
    if ((plain || '').includes('\n')) return true;
    if (!blocks || blocks.length === 0) return false;
    if (blocks.length > 1) return true;
    const first = blocks[0];
    return first.kind !== 'Paragraph' || hasInlineMarks(first.text);
  }

  // ------------------------------------------------------------------- blocks

  const attached = new WeakMap();

  function attachBlock(el, dotnet, blockId) {
    if (!el || attached.get(el) === blockId) return;
    attached.set(el, blockId);

    const report = () => {
      dotnet.invokeMethodAsync('OnInput', plainText(el), caretOffset(el));
    };

    let suppressInput = false;
    el.addEventListener('input', (event) => {
      if (event.isComposing || suppressInput) return;
      report();
    });

    el.addEventListener('compositionend', report);

    el.addEventListener('focus', () => {
      dotnet.invokeMethodAsync('OnFocus');
    });

    el.addEventListener('blur', () => {
      dotnet.invokeMethodAsync('OnBlur', plainText(el));
    });

    el.addEventListener('paste', async (event) => {
      const files = filesFrom(event.clipboardData);
      if (files.length > 0) {
        event.preventDefault();
        await dotnet.invokeMethodAsync('OnPasteFiles');
        assignInputFiles('syncly-attach', files);
        return;
      }

      const data = event.clipboardData || window.clipboardData;
      const html = data?.getData('text/html') || '';
      const plain = (data?.getData('text') || '').replace(/\r\n/g, '\n');

      if (isCodeBlock(el)) {
        event.preventDefault();
        suppressInput = true;
        insertPlain(el, plain, report);
        suppressInput = false;
        return;
      }

      const blocks = htmlToBlocks(html);

      if (!shouldPasteAsBlocks(blocks, plain)) {
        event.preventDefault();
        document.execCommand('insertText', false, plain);
        return;
      }

      event.preventDefault();
      const text = plainText(el);
      const caret = caretOffset(el);
      await dotnet.invokeMethodAsync(
        'OnPasteBlocks',
        JSON.stringify(blocks),
        plain,
        text,
        caret);
    });

    el.addEventListener('keydown', (event) => {
      const caret = caretOffset(el);
      const end = selectionEnd(el);
      const info = lineInfo(el, caret);
      const text = plainText(el);

      // While a menu is open it owns the navigation keys.
      if (state.menuOpen && ['ArrowUp', 'ArrowDown', 'Enter', 'Escape', 'Tab'].includes(event.key)) {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnMenuKey', event.key);
        return;
      }

      let handled = true;

      switch (event.key) {
        case 'Enter':
          if (isCodeBlock(el)) {
            if (shouldExitCode(text, caret, event)) {
              event.preventDefault();
              suppressInput = true;
              writePlain(el, text.slice(0, caret), caret);
              suppressInput = false;
              dotnet.invokeMethodAsync('OnSplit', text, caret);
            } else if (!isField(el)) {
              event.preventDefault();
              suppressInput = true;
              insertPlain(el, '\n', report);
              suppressInput = false;
            } else {
              handled = false;
            }
            break;
          }
          if (event.shiftKey) { handled = false; break; }
          event.preventDefault();
          // Truncate before the round-trip so an unmount blur cannot write the
          // suffix back onto this block after SplitBlockAsync has already moved it.
          suppressInput = true;
          writePlain(el, text.slice(0, caret), caret);
          suppressInput = false;
          dotnet.invokeMethodAsync('OnSplit', text, caret);
          break;

        case 'Backspace':
          if (caret !== 0 || end !== 0) { handled = false; break; }
          event.preventDefault();
          dotnet.invokeMethodAsync('OnMergeBackward');
          break;

        case 'Delete':
          if (!info.atEnd || end !== caret) { handled = false; break; }
          event.preventDefault();
          dotnet.invokeMethodAsync('OnMergeForward');
          break;

        case 'Tab':
          event.preventDefault();
          if (isCodeBlock(el) && !event.shiftKey) {
            suppressInput = true;
            insertPlain(el, '  ', report);
            suppressInput = false;
            break;
          }
          dotnet.invokeMethodAsync('OnIndent', !event.shiftKey);
          break;

        case 'ArrowUp':
          if (event.altKey) {
            event.preventDefault();
            dotnet.invokeMethodAsync('OnMoveBlock', true);
          } else if (info.atStart) {
            event.preventDefault();
            dotnet.invokeMethodAsync('OnStep', false, caret);
          } else {
            handled = false;
          }
          break;

        case 'ArrowDown':
          if (event.altKey) {
            event.preventDefault();
            dotnet.invokeMethodAsync('OnMoveBlock', false);
          } else if (info.atEnd) {
            event.preventDefault();
            dotnet.invokeMethodAsync('OnStep', true, caret);
          } else {
            handled = false;
          }
          break;

        case 'Escape':
          dotnet.invokeMethodAsync('OnEscape');
          handled = false;
          break;

        case 'b':
        case 'i':
        case 'u':
          if (isCodeBlock(el) || (!event.ctrlKey && !event.metaKey)) { handled = false; break; }
          event.preventDefault();
          dotnet.invokeMethodAsync('OnMark', event.key, text, caret, end);
          break;

        case 's':
        case 'S':
          if (isCodeBlock(el) || (!event.ctrlKey && !event.metaKey) || !event.shiftKey) { handled = false; break; }
          event.preventDefault();
          event.stopPropagation();
          dotnet.invokeMethodAsync('OnMark', 's', text, caret, end);
          break;

        default:
          handled = false;
      }

      if (!handled && event.key === '/' && !event.ctrlKey && !event.metaKey && !event.altKey
          && text.length === 0) {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnSlash');
      }
    });
  }

  function setText(id, text, caret) {
    const el = element(id);
    if (!el) return;
    if (plainText(el) === text) return;

    const focused = document.activeElement === el;
    const previous = focused ? caretOffset(el) : 0;
    writePlain(el, text, focused ? (caret >= 0 ? caret : Math.min(previous, text.length)) : undefined);
  }

  function focusBlock(id, caret) {
    const el = element(id);
    if (!el) return false;

    el.focus({ preventScroll: false });
    placeCaret(el, caret < 0 ? plainText(el).length : caret);
    return true;
  }

  function revealBlock(id) {
    const el = document.getElementById(id);
    if (!el) return false;

    el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    el.classList.remove('search-flash');
    void el.offsetWidth;
    el.classList.add('search-flash');
    el.addEventListener('animationend', () => el.classList.remove('search-flash'), { once: true });
    return true;
  }

  function getText(id) {
    const el = element(id);
    return el ? plainText(el) : '';
  }

  // Current text + selection for a block, so a toolbar button can toggle a mark without a keyboard.
  function selection(id) {
    const el = element(id);
    if (!el) return { text: '', start: 0, end: 0 };
    return { text: plainText(el), start: caretOffset(el), end: selectionEnd(el) };
  }

  function getCaret(id) {
    const el = element(id);
    return el ? caretOffset(el) : 0;
  }

  // Where to hang a popup so it follows the block instead of floating in the middle of the
  // screen. Fitting it to the viewport is clampFloating's job once the popup has a size.
  function anchorOf(id) {
    const el = element(id);
    if (!el) return null;

    const rect = el.getBoundingClientRect();
    return { left: Math.max(8, rect.left), top: rect.bottom + 6 };
  }

  // -------------------------------------------------------------------- shell

  function filesFrom(data) {
    if (!data) return [];
    if (data.files && data.files.length) return Array.from(data.files);
    const out = [];
    for (const item of data.items || []) {
      if (item.kind === 'file') {
        const file = item.getAsFile();
        if (file) out.push(file);
      }
    }
    return out;
  }

  function assignInputFiles(inputId, files) {
    const input = element(inputId);
    if (!input || !files.length) return false;
    const transfer = new DataTransfer();
    for (const file of files) transfer.items.add(file);
    input.files = transfer.files;
    input.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  }

  function hasFiles(event) {
    const types = event.dataTransfer && event.dataTransfer.types;
    if (!types) return false;
    return Array.from(types).includes('Files');
  }

  function attachDropTarget(elId, inputId, dotnet) {
    const el = element(elId);
    if (!el || el.dataset.synclyDrop === '1') return;
    el.dataset.synclyDrop = '1';

    el.addEventListener('dragenter', (event) => {
      if (!el.classList.contains('can-drop') || !hasFiles(event)) return;
      event.preventDefault();
      el.classList.add('drop-hover');
    });

    el.addEventListener('dragover', (event) => {
      if (!el.classList.contains('can-drop') || !hasFiles(event)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'copy';
    });

    el.addEventListener('dragleave', (event) => {
      if (!el.contains(event.relatedTarget)) el.classList.remove('drop-hover');
    });

    el.addEventListener('drop', async (event) => {
      el.classList.remove('drop-hover');
      if (!el.classList.contains('can-drop')) return;
      const files = filesFrom(event.dataTransfer);
      if (files.length === 0) return;
      event.preventDefault();
      if (dotnet) await dotnet.invokeMethodAsync('OnDropFiles');
      assignInputFiles(inputId, files);
    });
  }

  function clearInput(id) {
    const el = element(id);
    if (el) el.value = '';
  }

  function attachShell(dotnet) {
    if (state.shell) return;
    state.shell = dotnet;

    document.addEventListener('dragstart', (event) => {
      const row = event.target.closest?.('.tree-row[data-page]');
      if (!row || !event.dataTransfer) return;
      event.dataTransfer.setData('text/plain', row.dataset.page);
      event.dataTransfer.effectAllowed = 'move';
    });

    document.addEventListener('keydown', (event) => {
      const meta = event.ctrlKey || event.metaKey;

      if (meta && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnPalette');
        return;
      }

      if (meta && event.key.toLowerCase() === 'e') {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnToggleReading');
        return;
      }

      if (meta && event.key.toLowerCase() === 'n') {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnNewPage');
        return;
      }

      if (meta && event.key.toLowerCase() === 'f') {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnFind');
        return;
      }

      if (meta && event.key.toLowerCase() === 'd') {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnDailyNote');
        return;
      }

      if (event.key === 'Escape' && (state.paletteOpen || state.menuOpen)) {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnEscape');
      }
    });

    // Capture so a wikilink click does not also focus the surrounding preview block.
    document.addEventListener('click', (event) => {
      const link = event.target.closest('a.wikilink');
      if (!link) return;

      event.preventDefault();
      event.stopPropagation();
      dotnet.invokeMethodAsync('OnOpenLink', link.dataset.page ?? link.textContent ?? '');
    }, true);

    attachLinkPreview(dotnet);
  }

  // Hover a wikilink a moment and a small card shows the target's title and first lines.
  function attachLinkPreview(dotnet) {
    let card = null;
    let timer = null;
    let current = null;

    const hide = () => {
      clearTimeout(timer);
      timer = null;
      current = null;
      card?.remove();
      card = null;
    };

    const show = async (link) => {
      const title = link.dataset.page ?? link.textContent ?? '';
      let info;
      try {
        info = await dotnet.invokeMethodAsync('OnLinkPreview', title);
      } catch {
        return;
      }
      if (!info || current !== link) return;

      card = document.createElement('div');
      card.className = 'link-preview';
      const rect = link.getBoundingClientRect();
      card.style.left = `${Math.max(8, Math.min(rect.left, window.innerWidth - 320))}px`;
      card.style.top = `${rect.bottom + 8}px`;

      const heading = document.createElement('strong');
      heading.textContent = info.missing ? `${info.title} (not created yet)` : info.title;
      card.appendChild(heading);

      for (const line of info.lines ?? []) {
        const p = document.createElement('p');
        p.textContent = line;
        card.appendChild(p);
      }

      document.body.appendChild(card);
    };

    document.addEventListener('mouseover', (event) => {
      const link = event.target.closest?.('a.wikilink');
      if (!link || link === current) return;
      hide();
      current = link;
      timer = setTimeout(() => show(link), 350);
    });

    document.addEventListener('mouseout', (event) => {
      const link = event.target.closest?.('a.wikilink');
      if (!link || link !== current) return;
      if (event.relatedTarget && link.contains(event.relatedTarget)) return;
      hide();
    });

    document.addEventListener('scroll', hide, true);
  }

  function setMenuOpen(open) {
    state.menuOpen = open;
  }

  function setPaletteOpen(open) {
    state.paletteOpen = open;
  }

  function focusElement(id, select) {
    const el = element(id);
    if (!el) return;
    el.focus();
    if (select && el.select) el.select();
  }

  function isNarrow() {
    return window.matchMedia('(max-width: 720px)').matches;
  }

  function treeDropHint(pageId, clientX, clientY) {
    const row = document.querySelector(`.tree-row[data-page="${CSS.escape(pageId)}"]`);
    if (!row) return 'after';

    const hit = document.elementFromPoint(clientX, clientY);
    if (hit && row.contains(hit) && hit.closest('.tree-label, .tree-icon'))
      return 'into';

    const rect = row.getBoundingClientRect();
    if (rect.height <= 0) return 'after';
    return clientY < rect.top + rect.height / 2 ? 'before' : 'after';
  }

  // Anything anchored to a click or a block is placed first and fitted afterwards, so the
  // caller never has to guess how tall the popup will turn out to be.
  function clampFloating(selector, focus) {
    const el = document.querySelector(selector);
    if (!el) return;

    const pad = 8;
    const rect = el.getBoundingClientRect();
    let left = rect.left;
    let top = rect.top;

    if (left + rect.width > window.innerWidth - pad)
      left = Math.max(pad, window.innerWidth - rect.width - pad);
    if (top + rect.height > window.innerHeight - pad)
      top = Math.max(pad, window.innerHeight - rect.height - pad);
    if (left < pad) left = pad;
    if (top < pad) top = pad;

    el.style.left = `${left}px`;
    el.style.top = `${top}px`;

    if (focus && typeof el.focus === 'function') {
      el.tabIndex = -1;
      el.focus({ preventScroll: true });
    }
  }

  function blockDropHint(blockId, clientX, clientY) {
    const el = document.getElementById('blk-' + blockId);
    if (!el) return 'after';
    const rect = el.getBoundingClientRect();
    if (rect.height <= 0) return 'after';
    return clientY < rect.top + rect.height / 2 ? 'before' : 'after';
  }

  function confirmDialog(message) {
    return window.confirm(message);
  }

  function downloadText(fileName, text) {
    const blob = new Blob([text], { type: 'text/markdown;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  function printPage() {
    window.print();
  }

  async function scanQr() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      return null;
    }

    const Detector = window.BarcodeDetector;
    const detector = Detector ? new Detector({ formats: ['qr_code'] }) : null;
    if (!detector) return null;

    const stream = await navigator.mediaDevices.getUserMedia({
      video: { facingMode: 'environment' },
    });

    const overlay = document.createElement('div');
    overlay.className = 'qr-scan';
    const video = document.createElement('video');
    video.autoplay = true;
    video.playsInline = true;
    video.muted = true;
    video.srcObject = stream;
    const cancel = document.createElement('button');
    cancel.className = 'chip';
    cancel.textContent = 'Cancel';
    overlay.append(video, cancel);
    document.body.appendChild(overlay);
    await video.play().catch(() => {});

    return await new Promise((resolve) => {
      let done = false;
      const finish = (value) => {
        if (done) return;
        done = true;
        clearInterval(timer);
        stream.getTracks().forEach((track) => track.stop());
        overlay.remove();
        resolve(value);
      };

      cancel.addEventListener('click', () => finish(null));
      const timer = setInterval(async () => {
        try {
          const codes = await detector.detect(video);
          if (codes && codes.length > 0 && codes[0].rawValue) {
            finish(codes[0].rawValue);
          }
        } catch {
          // Keep scanning until cancel.
        }
      }, 250);
    });
  }

    window.syncly = {
    attachBlock,
    attachDropTarget,
    attachShell,
    clearInput,
    setText,
    focusBlock,
    revealBlock,
    focusElement,
    getText,
    getCaret,
    selection,
    anchorOf,
    setMenuOpen,
    setPaletteOpen,
    isNarrow,
    confirm: confirmDialog,
    treeDropHint,
    clampFloating,
    blockDropHint,
    scanQr,
    downloadText,
    printPage,
    showPicker(el) {
      if (!el) return;
      if (typeof el.showPicker === 'function') {
        try {
          el.showPicker();
          return;
        } catch {
          // Not a user gesture, or the control is disabled.
        }
      }
      el.focus();
    },
    click(id) {
      const el = element(id);
      if (el) el.click();
    },
    createObjectUrl(bytes, mime) {
      let array;
      if (typeof bytes === "string") {
        const bin = atob(bytes);
        array = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) array[i] = bin.charCodeAt(i);
      } else {
        array = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
      }
      const blob = new Blob([array], { type: mime || "application/octet-stream" });
      return URL.createObjectURL(blob);
    },
    revokeObjectUrl(url) {
      if (url) URL.revokeObjectURL(url);
    },
  };
})();
