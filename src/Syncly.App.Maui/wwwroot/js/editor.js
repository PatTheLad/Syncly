window.synclyEditor = {
  wrapSelection: function (id, before, after) {
    const el = document.getElementById(id);
    if (!el) return el?.value ?? '';
    const start = el.selectionStart ?? 0;
    const end = el.selectionEnd ?? 0;
    const value = el.value;
    const selected = value.substring(start, end) || 'text';
    const next = value.substring(0, start) + before + selected + after + value.substring(end);
    el.value = next;
    const caret = start + before.length + selected.length + after.length;
    el.focus();
    el.setSelectionRange(start + before.length, start + before.length + selected.length);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    return next;
  },
  insertLinePrefix: function (id, prefix) {
    const el = document.getElementById(id);
    if (!el) return '';
    const start = el.selectionStart ?? 0;
    const value = el.value;
    const lineStart = value.lastIndexOf('\n', Math.max(0, start - 1)) + 1;
    const next = value.substring(0, lineStart) + prefix + value.substring(lineStart);
    el.value = next;
    const caret = start + prefix.length;
    el.focus();
    el.setSelectionRange(caret, caret);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    return next;
  }
};
