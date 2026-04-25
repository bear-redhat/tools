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
    const rect = container.getBoundingClientRect();
    let pct;
    if (isCol) {
      pct = ((clientPos - rect.left) / rect.width) * 100;
    } else {
      pct = ((clientPos - rect.top) / rect.height) * 100;
    }
    pct = Math.min(Math.max(pct, minPct), maxPct);
    before.style.flexBasis = pct + '%';
    after.style.flexBasis = (100 - pct) + '%';
    if (isCol) after.style.width = 'auto';
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
