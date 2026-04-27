const scrollState = new WeakMap();

window.initAutoScroll = (element) => {
  if (!element) return;
  scrollState.set(element, true);
  element.addEventListener('scroll', () => {
    const atBottom =
      element.scrollHeight - element.scrollTop - element.clientHeight <= 150;
    scrollState.set(element, atBottom);
  });
};

window.scrollToBottom = (element) => {
  if (!element) return;
  if (scrollState.get(element) !== false) {
    element.scrollTop = element.scrollHeight;
  }
};

window.forceScrollToBottom = (element) => {
  if (!element) return;
  element.scrollTop = element.scrollHeight;
  scrollState.set(element, true);
};

window.initDividerResize = (divider, direction) => {
  if (!divider) return false;
  const container = divider.parentElement;
  const before = divider.previousElementSibling;
  const after = divider.nextElementSibling;
  if (!container || !before || !after) return false;

  const isCol = direction === 'col';
  const minPct = isCol ? 25 : 20;
  const maxPct = isCol ? 75 : 80;
  let dragging = false;

  const applyResize = (clientPos) => {
    if (isCol) {
      const rect = container.getBoundingClientRect();
      let pct = ((clientPos - rect.left) / rect.width) * 100;
      pct = Math.min(Math.max(pct, minPct), maxPct);
      before.style.flexBasis = pct + '%';
      after.style.flexBasis = (100 - pct) + '%';
      after.style.width = 'auto';
    } else {
      const beforeRect = before.getBoundingClientRect();
      const afterRect = after.getBoundingClientRect();
      const available = beforeRect.height + afterRect.height;
      const beforeH = clientPos - beforeRect.top;
      const minH = available * minPct / 100;
      const maxH = available * maxPct / 100;
      const clamped = Math.min(Math.max(beforeH, minH), maxH);
      before.style.flexBasis = clamped + 'px';
      after.style.flexBasis = (available - clamped) + 'px';
    }
  };

  const resizingClass = isCol ? 'resizing' : 'resizing-row';

  divider.addEventListener('mousedown', (e) => {
    e.preventDefault();
    dragging = true;
    container.classList.add(resizingClass);
  });

  divider.addEventListener('touchstart', (e) => {
    e.preventDefault();
    dragging = true;
    container.classList.add(resizingClass);
  }, { passive: false });

  window.addEventListener('mousemove', (e) => {
    if (!dragging) return;
    applyResize(isCol ? e.clientX : e.clientY);
  });

  window.addEventListener('touchmove', (e) => {
    if (!dragging) return;
    const t = e.touches[0];
    applyResize(isCol ? t.clientX : t.clientY);
  }, { passive: true });

  window.addEventListener('mouseup', () => {
    if (!dragging) return;
    dragging = false;
    container.classList.remove(resizingClass);
  });

  window.addEventListener('touchend', () => {
    if (!dragging) return;
    dragging = false;
    container.classList.remove(resizingClass);
  });

  return true;
};

window.playCaseClosedSound = () => {
  const ctx = new (window.AudioContext || window.webkitAudioContext)();
  const t = ctx.currentTime;
  const notes = [
    { freq: 329.63, start: 0,    dur: 0.12 }, // E4
    { freq: 392.00, start: 0.10, dur: 0.12 }, // G4
    { freq: 493.88, start: 0.20, dur: 0.12 }, // B4
    { freq: 659.25, start: 0.30, dur: 0.15 }, // E5
    { freq: 783.99, start: 0.42, dur: 0.35 }, // G5 (resolve)
  ];
  for (const n of notes) {
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'triangle';
    osc.frequency.value = n.freq;
    const onset = t + n.start;
    gain.gain.setValueAtTime(0, onset);
    gain.gain.linearRampToValueAtTime(0.25, onset + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.001, onset + n.dur);
    osc.connect(gain).connect(ctx.destination);
    osc.start(onset);
    osc.stop(onset + n.dur);
  }
};

window.initPasteHandler = (textarea) => {
  if (!textarea) return;
  textarea.addEventListener('paste', (e) => {
    const html = e.clipboardData?.getData('text/html');
    if (!html) return;

    const doc = new DOMParser().parseFromString(html, 'text/html');
    if (!doc.body.querySelector('a[href]')) return;

    const convert = (node) => {
      if (node.nodeType === Node.TEXT_NODE) return node.textContent;
      if (node.nodeType !== Node.ELEMENT_NODE) return '';

      if (node.tagName === 'A' && node.href) {
        const text = node.textContent.trim();
        const url = node.getAttribute('href');
        if (!text || text === url) return url;
        return `[${text}](${url})`;
      }

      if (node.tagName === 'BR') return '\n';

      let result = '';
      for (const child of node.childNodes) result += convert(child);

      const block = /^(P|DIV|LI|TR|H[1-6]|BLOCKQUOTE)$/.test(node.tagName);
      if (block) result = result.trim() + '\n';

      return result;
    };

    const text = convert(doc.body).replace(/\n{3,}/g, '\n\n').trim();
    if (!text) return;

    e.preventDefault();
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const before = textarea.value.substring(0, start);
    const after = textarea.value.substring(end);
    textarea.value = before + text + after;
    textarea.selectionStart = textarea.selectionEnd = start + text.length;
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
  });
};
