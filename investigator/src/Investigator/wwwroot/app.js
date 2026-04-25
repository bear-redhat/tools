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
