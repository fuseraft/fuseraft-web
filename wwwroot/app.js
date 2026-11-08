// Chat: Enter sends and Shift+Enter starts a new line, as in the VS Code panel. The page sends on
// Enter; this only keeps the newline out of the box, and holds back the Enter that finishes an IME
// composition so it doesn't send a half-typed message.
window.addEventListener('keydown', e => {
  if (!e.target.classList?.contains('repl-textarea')) return;
  if (e.key !== 'Enter' || e.shiftKey || e.altKey) return;
  if (e.isComposing) { e.stopPropagation(); return; }
  e.preventDefault();
}, true);

// Chat: keep the newest message in view, unless the reader has scrolled up to read.
let followChat = true;
document.addEventListener('scroll', e => {
  const main = e.target;
  if (main.classList?.contains('main-content'))
    followChat = main.scrollHeight - main.scrollTop - main.clientHeight < 80;
}, true);
new MutationObserver(() => {
  const main = document.querySelector('.main-content');
  if (followChat && main?.querySelector('.repl-transcript')) main.scrollTop = main.scrollHeight;
}).observe(document.body, { childList: true, subtree: true, characterData: true });

// The address the browser shows, which Routes compares with the one its circuit started at.
window.fuseraftAddress = () => location.href;
