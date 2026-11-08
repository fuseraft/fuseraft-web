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

// Notifications: a toast unless the reader is already on the page it's about, and, while they're looking
// elsewhere, a count in the tab title and a desktop notification if they turned those on. The server only
// sends a tab what its user may see; which groups to show is a choice per browser, kept here.
window.fuseraftNotify = (() => {
  const key = 'fuseraft.notify';
  const defaults = { desktop: false, attention: true, results: true, replies: true };
  let prefs = { ...defaults };
  try { prefs = { ...defaults, ...JSON.parse(localStorage.getItem(key) || '{}') }; } catch { }
  const save = () => { try { localStorage.setItem(key, JSON.stringify(prefs)); } catch { } };

  const supported = () => 'Notification' in window && window.isSecureContext;
  const looking = () => document.visibilityState === 'visible' && document.hasFocus();
  const go = link => { window.focus(); window.Blazor ? Blazor.navigateTo(link) : location.assign(link); };

  let unread = 0, baseTitle = document.title;
  const setTitle = () => {
    const want = unread ? `(${unread}) ${baseTitle}` : baseTitle;
    if (document.title !== want) document.title = want;
  };
  // Pages set their own titles as you move around; keep the count in front of whichever is current.
  new MutationObserver(() => {
    const t = document.title.replace(/^\(\d+\) /, '');
    if (t !== baseTitle) { baseTitle = t; setTitle(); }
  }).observe(document.head, { childList: true, subtree: true, characterData: true });
  const seen = () => { if (looking() && unread) { unread = 0; setTitle(); } };
  document.addEventListener('visibilitychange', seen);
  window.addEventListener('focus', seen);

  let stack;
  const toast = n => {
    if (!stack?.isConnected) {
      stack = document.createElement('div');
      stack.className = 'toasts';
      stack.setAttribute('role', 'status');
      stack.setAttribute('aria-live', 'polite');
      document.body.appendChild(stack);
    }
    const el = document.createElement('div');
    el.className = `toast toast-${n.level}`;
    const open = document.createElement('button');
    open.className = 'toast-open';
    const title = document.createElement('strong');
    title.textContent = n.title;
    open.appendChild(title);
    if (n.body) {
      const body = document.createElement('span');
      body.textContent = n.body;
      open.appendChild(body);
    }
    const close = document.createElement('button');
    close.className = 'toast-close';
    close.setAttribute('aria-label', 'Dismiss');
    close.textContent = '×';
    const remove = () => el.remove();
    open.onclick = () => { remove(); go(n.link); };
    close.onclick = remove;
    el.append(open, close);
    stack.appendChild(el);
    while (stack.children.length > 4) stack.firstChild.remove();
    // Something waiting on an answer stays until it's dealt with; the rest fade once read.
    if (n.category !== 'attention') {
      const fade = () => looking() ? setTimeout(remove, 8000) : setTimeout(fade, 1000);
      fade();
    }
  };

  const desktop = n => {
    if (!prefs.desktop || !supported() || Notification.permission !== 'granted') return;
    try {
      // The tag keeps several open tabs from each showing the same one.
      const d = new Notification(n.title, { body: n.body, tag: n.id, icon: 'icon.png' });
      d.onclick = () => { d.close(); go(n.link); };
    } catch { }
  };

  const api = {
    show(n) {
      if (!prefs[n.category]) return;
      if (!n.onPage) toast(n);
      if (!looking()) { unread++; setTitle(); desktop(n); }
    },
    state: () => ({
      ...prefs,
      supported: 'Notification' in window,
      secure: window.isSecureContext,
      permission: 'Notification' in window ? Notification.permission : 'unsupported',
    }),
    async enableDesktop() {
      if (!supported()) return api.state();
      if (Notification.permission === 'default') await Notification.requestPermission();
      prefs.desktop = Notification.permission === 'granted';
      save();
      return api.state();
    },
    set(name, value) {
      if (name in defaults) { prefs[name] = !!value; save(); }
      return api.state();
    },
  };
  return api;
})();
