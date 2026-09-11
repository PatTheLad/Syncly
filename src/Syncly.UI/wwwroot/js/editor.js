// The editor keeps the CRDT authoritative in C#. This file owns only what the DOM must own:
// caret position, key interception, and writing text back into a contenteditable without
// destroying the selection.
(function () {
  const state = {
    menuOpen: false,
    paletteOpen: false,
    shell: null,
  };

  function element(id) {
    return document.getElementById(id);
  }

  // ---------------------------------------------------------------- selection

  function caretOffset(node) {
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

  function lineInfo(node, offset) {
    const text = node.textContent;
    return {
      atStart: offset === 0,
      atEnd: offset >= text.length,
      empty: text.length === 0,
    };
  }

  // ------------------------------------------------------------------- blocks

  const attached = new WeakMap();

  function attachBlock(el, dotnet, blockId) {
    if (!el || attached.get(el) === blockId) return;
    attached.set(el, blockId);

    const report = () => {
      dotnet.invokeMethodAsync('OnInput', el.textContent ?? '', caretOffset(el));
    };

    el.addEventListener('input', (event) => {
      if (event.isComposing) return;
      report();
    });

    el.addEventListener('compositionend', report);

    el.addEventListener('focus', () => {
      dotnet.invokeMethodAsync('OnFocus');
    });

    el.addEventListener('blur', () => {
      dotnet.invokeMethodAsync('OnBlur', el.textContent ?? '');
    });

    el.addEventListener('paste', async (event) => {
      const files = filesFrom(event.clipboardData);
      if (files.length > 0) {
        event.preventDefault();
        await dotnet.invokeMethodAsync('OnPasteFiles');
        assignInputFiles('syncly-attach', files);
        return;
      }

      // Paste as plain text; the block model has no place for foreign markup.
      event.preventDefault();
      const text = (event.clipboardData || window.clipboardData).getData('text');
      document.execCommand('insertText', false, text.replace(/\r\n/g, '\n'));
    });

    el.addEventListener('keydown', (event) => {
      const caret = caretOffset(el);
      const end = selectionEnd(el);
      const info = lineInfo(el, caret);
      const text = el.textContent ?? '';

      // While a menu is open it owns the navigation keys.
      if (state.menuOpen && ['ArrowUp', 'ArrowDown', 'Enter', 'Escape', 'Tab'].includes(event.key)) {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnMenuKey', event.key);
        return;
      }

      let handled = true;

      switch (event.key) {
        case 'Enter':
          if (event.shiftKey) { handled = false; break; }
          event.preventDefault();
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
          if (!event.ctrlKey && !event.metaKey) { handled = false; break; }
          event.preventDefault();
          dotnet.invokeMethodAsync('OnMark', event.key, text, caret, end);
          break;

        default:
          handled = false;
      }

      if (!handled && event.key === '/' && text.length === 0) {
        dotnet.invokeMethodAsync('OnSlash');
      }
    });
  }

  function setText(id, text, caret) {
    const el = element(id);
    if (!el) return;
    if ((el.textContent ?? '') === text) return;

    const focused = document.activeElement === el;
    const previous = focused ? caretOffset(el) : 0;

    el.textContent = text;

    if (focused) {
      placeCaret(el, caret >= 0 ? caret : Math.min(previous, text.length));
    }
  }

  function focusBlock(id, caret) {
    const el = element(id);
    if (!el) return false;

    el.focus({ preventScroll: false });
    placeCaret(el, caret < 0 ? (el.textContent ?? '').length : caret);
    return true;
  }

  function getText(id) {
    const el = element(id);
    return el ? el.textContent ?? '' : '';
  }

  function getCaret(id) {
    const el = element(id);
    return el ? caretOffset(el) : 0;
  }

  // Where to hang a popup so it follows the block instead of floating in the middle of the screen.
  function anchorOf(id) {
    const el = element(id);
    if (!el) return null;

    const rect = el.getBoundingClientRect();
    const left = Math.min(rect.left, window.innerWidth - 280);
    const below = rect.bottom + 6;
    const fits = below + 320 < window.innerHeight;

    return { left: Math.max(8, left), top: fits ? below : Math.max(8, rect.top - 326) };
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

      if (meta && event.key.toLowerCase() === 's') {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnSyncNow');
        return;
      }

      if (event.key === 'Escape' && (state.paletteOpen || state.menuOpen)) {
        event.preventDefault();
        dotnet.invokeMethodAsync('OnEscape');
      }
    });

    // Wikilinks are rendered as anchors; routing them stays in C#.
    document.addEventListener('click', (event) => {
      const link = event.target.closest('a.wikilink');
      if (!link) return;

      event.preventDefault();
      dotnet.invokeMethodAsync('OnOpenLink', link.dataset.page ?? link.textContent ?? '');
    });
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

  function confirmDialog(message) {
    return window.confirm(message);
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
    focusElement,
    getText,
    getCaret,
    anchorOf,
    setMenuOpen,
    setPaletteOpen,
    isNarrow,
    confirm: confirmDialog,
    scanQr,
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
