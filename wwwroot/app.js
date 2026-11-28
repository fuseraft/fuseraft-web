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

// Chat composer: a menu of slash commands as you type "/", and of workspace files after "@"; images
// pasted or dropped into the box go to the Attach input; and /copy, Export and code-block copy buttons.
// The menu lives on <body>, outside what Blazor renders, so its DOM updates never meet Blazor's.
window.fuseraftChat = (() => {
  const menu = document.createElement('div');
  menu.className = 'composer-menu';
  menu.setAttribute('role', 'listbox');
  menu.hidden = true;
  let active = null;   // { ta, dotnet, items, index, from }
  let seq = 0, timer = 0;

  const close = () => { menu.hidden = true; if (active) active.items = []; };

  const place = ta => {
    const r = ta.getBoundingClientRect();
    menu.style.left = `${r.left}px`;
    menu.style.width = `${Math.min(r.width, 680)}px`;
    menu.style.bottom = `${window.innerHeight - r.top + 6}px`;
  };

  const render = () => {
    const a = active;
    if (!a || !a.items.length) { close(); return; }
    if (!menu.isConnected) document.body.appendChild(menu);
    menu.replaceChildren(...a.items.map((item, i) => {
      const row = document.createElement('div');
      row.className = 'composer-item' + (i === a.index ? ' active' : '');
      row.setAttribute('role', 'option');
      row.setAttribute('aria-selected', i === a.index);
      const label = document.createElement('span');
      label.className = 'composer-label';
      label.textContent = item.label;
      row.appendChild(label);
      if (item.detail) {
        const detail = document.createElement('span');
        detail.className = 'composer-detail';
        detail.textContent = item.detail;
        row.appendChild(detail);
      }
      row.addEventListener('mousedown', e => { e.preventDefault(); pick(i); });
      return row;
    }));
    place(a.ta);
    menu.hidden = false;
    menu.children[a.index]?.scrollIntoView({ block: 'nearest' });
  };

  const commands = ta => {
    try { return JSON.parse(ta.dataset.commands || '[]'); } catch { return []; }
  };

  const update = () => {
    const a = active;
    if (!a) return;
    const ta = a.ta, caret = ta.selectionStart, before = ta.value.slice(0, caret);
    let m;
    if ((m = /^\/(\S*)$/.exec(before))) {
      const q = m[1].toLowerCase();
      a.from = 0;
      a.items = commands(ta)
        .filter(c => c.u.startsWith('/') && c.u.slice(1).toLowerCase().startsWith(q))
        .slice(0, 40)
        .map(c => {
          const name = c.u.split(' ')[0];
          return { label: c.u, detail: c.d, insert: name + (c.u.includes(' ') ? ' ' : '') };
        });
      a.index = 0;
      render();
    } else if (ta.dataset.mentions === 'on' && (m = /(^|[\s(\[])@([^\s@]*)$/.exec(before))) {
      const q = m[2], from = caret - q.length - 1, mine = ++seq;
      clearTimeout(timer);
      timer = setTimeout(async () => {
        let paths = [];
        try { paths = await a.dotnet.invokeMethodAsync('SuggestFiles', q); } catch { }
        if (mine !== seq || active !== a) return;
        a.from = from;
        a.items = paths.map(p => ({ label: p, insert: '@' + p + (p.endsWith('/') ? '' : ' '), more: p.endsWith('/') }));
        a.index = 0;
        render();
      }, 120);
    } else {
      seq++;
      close();
    }
  };

  const pick = i => {
    const a = active, item = a?.items[i];
    if (!item) return;
    const ta = a.ta, caret = ta.selectionStart;
    ta.value = ta.value.slice(0, a.from) + item.insert + ta.value.slice(caret);
    const at = a.from + item.insert.length;
    ta.setSelectionRange(at, at);
    ta.dispatchEvent(new Event('input', { bubbles: true }));
    ta.focus();
    if (item.more) update(); else close();
  };

  // What's typed is already the highlighted command, so picking it would only add a space.
  const typedWhole = a => {
    const item = a.items[a.index];
    return !!item && a.from === 0 && item.insert.trim().toLowerCase() === a.ta.value.slice(0, a.ta.selectionStart).trim().toLowerCase();
  };

  // Ahead of the page's own Enter-to-send, so picking from the menu doesn't send the message.
  window.addEventListener('keydown', e => {
    const a = active;
    if (!a || e.target !== a.ta || menu.hidden || !a.items.length) return;
    const stop = () => { e.preventDefault(); e.stopImmediatePropagation(); };
    if (e.key === 'ArrowDown') { stop(); a.index = (a.index + 1) % a.items.length; render(); }
    else if (e.key === 'ArrowUp') { stop(); a.index = (a.index - 1 + a.items.length) % a.items.length; render(); }
    else if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && typedWhole(a)) close();   // already complete: let it send
    else if ((e.key === 'Enter' && !e.shiftKey && !e.isComposing) || e.key === 'Tab') { stop(); pick(a.index); }
    else if (e.key === 'Escape') { stop(); close(); }
  }, true);
  window.addEventListener('resize', () => { if (active && !menu.hidden) place(active.ta); });
  document.addEventListener('scroll', () => { if (active && !menu.hidden) place(active.ta); }, true);

  const attachImages = files => {
    const images = [...files].filter(f => f.type.startsWith('image/'));
    const input = document.getElementById('repl-attach');
    if (!images.length || !input) return false;
    const dt = new DataTransfer();
    images.forEach(f => dt.items.add(f));
    input.files = dt.files;
    input.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  };

  const attach = (ta, dotnet) => {
    if (ta.__fuseraftChat) { ta.__fuseraftChat.dotnet = dotnet; return; }
    const a = { ta, dotnet, items: [], index: 0, from: 0 };
    ta.__fuseraftChat = a;
    ta.addEventListener('input', () => { active = a; update(); });
    ta.addEventListener('click', () => { active = a; update(); });
    ta.addEventListener('focus', () => { active = a; });
    ta.addEventListener('blur', () => setTimeout(() => { if (document.activeElement !== ta) close(); }, 100));
    ta.addEventListener('paste', e => {
      const hasText = e.clipboardData?.types.includes('text/plain');
      if (attachImages(e.clipboardData?.files ?? []) && !hasText) e.preventDefault();
    });
    ta.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes('Files')) e.preventDefault(); });
    ta.addEventListener('drop', e => { if (attachImages(e.dataTransfer?.files ?? [])) e.preventDefault(); });
  };

  const copy = async text => {
    try { await navigator.clipboard.writeText(text); return true; }
    catch {
      const t = document.createElement('textarea');
      t.value = text;
      t.style.position = 'fixed'; t.style.opacity = '0';
      document.body.appendChild(t);
      t.select();
      const ok = document.execCommand('copy');
      t.remove();
      return ok;
    }
  };

  const download = (name, text) => {
    const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };

  // A copy button on every code block in rendered Markdown.
  const decorate = () => {
    for (const pre of document.querySelectorAll('.md pre:not([data-copy])')) {
      pre.dataset.copy = '1';
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'code-copy';
      button.textContent = 'Copy';
      button.addEventListener('click', async () => {
        const code = pre.querySelector('code')?.textContent ?? pre.textContent;
        button.textContent = await copy(code) ? 'Copied' : 'Copy failed';
        setTimeout(() => { button.textContent = 'Copy'; }, 1500);
      });
      pre.appendChild(button);
    }
  };
  new MutationObserver(decorate).observe(document.body, { childList: true, subtree: true });

  return { attach, copy, download };
})();
