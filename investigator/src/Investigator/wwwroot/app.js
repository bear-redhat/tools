window.scrollToBottom = (element) => {
  if (!element) return;
  const threshold = 50;
  const isAtBottom = element.scrollHeight - element.scrollTop - element.clientHeight <= threshold;
  if (isAtBottom) {
    element.scrollTop = element.scrollHeight;
  }
};

window.forceScrollToBottom = (element) => {
  if (!element) return;
  element.scrollTop = element.scrollHeight;
};
