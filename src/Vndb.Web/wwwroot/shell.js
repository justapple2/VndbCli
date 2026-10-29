'use strict';
// Navigation is presentation only; constructing the shell never queries or writes data.
window.VnShell = (() => {
  const mainLinks = [
    ['shelf', '/manage.html', '我的书架', 'book'],
    ['local', '/?view=local', '公共资料目录', 'grid'],
    ['plays', '/?view=plays', '游玩记录', 'clock'],
    ['quality', '/quality.html', '资料核对', 'check']
  ];
  const toolLinks = [
    ['remote', '/?view=remote', '查询与收录', 'search'],
    ['imports', '/manage.html?tab=imports', 'JSON 导入', 'import'],
    ['review', '/manage.html?tab=review', '资料候选', 'check'],
    ['trash', '/manage.html?tab=trash', '已移除记录', 'book'],
    ['catalog', '/?catalog=1', '资料补全', 'search'],
    ['sessions', '/sessions.html', '会话与离线活动', 'clock']
  ];
  const paths = {
    book: 'M4 3h7a3 3 0 0 1 3 3v15a4 4 0 0 0-4-2H4z M14 6h6v13h-6',
    grid: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
    clock: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18 M12 7v5l3 2',
    check: 'M4 4h16v16H4z M7 12l3 3 7-7',
    search: 'M10 3a7 7 0 1 0 0 14 7 7 0 0 0 0-14 M15 15l6 6',
    import: 'M12 3v12 M7 10l5 5 5-5 M4 16v5h16v-5',
    menu: 'M4 6h16 M4 12h16 M4 18h16'
  };
  function element(tag, cls, text) {
    const e = document.createElement(tag); if (cls) e.className = cls; if (text) e.textContent = text; return e;
  }
  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS(svg.namespaceURI, 'path'); path.setAttribute('d', paths[name] || paths.book); svg.append(path); return svg;
  }
  const isIndex = document.body.dataset.page === 'catalogue';
  let current = document.body.dataset.page || 'local';
  function navigation(mobile = false) {
    const nav = element('nav', 'site-nav'); nav.setAttribute('aria-label', mobile ? '移动导航' : '主导航');
    function link(row, parent) {
      const [id, href, label, glyph] = row, a = element('a', 'nav-item');
      a.href = href; a.dataset.destination = id; a.append(icon(glyph), element('span', '', label));
      if (!mobile && isIndex && ['local', 'remote', 'plays', 'catalog'].includes(id)) a.id = {local:'all-nav', remote:'remote-nav', plays:'plays-nav', catalog:'catalog-nav'}[id];
      parent.append(a);
    }
    mainLinks.forEach(row => link(row, nav));
    const tools = element('details', 'site-tools'), summary = element('summary', '', '工具'); tools.append(summary);
    toolLinks.forEach(row => link(row, tools)); nav.append(tools); return nav;
  }
  let aside = document.querySelector('.sidebar');
  if (!aside) { aside = element('aside', 'sidebar'); document.body.prepend(aside); }
  const brand = element('a', 'brand'); brand.href = '/manage.html'; brand.append(icon('book'), element('span', '', 'VNDB Local'));
  aside.prepend(brand, navigation());
  let workspace = document.querySelector('.workspace');
  if (!workspace) {
    workspace = element('div', 'workspace'); const main = document.querySelector('main'); main.before(workspace); workspace.append(main);
    const header = element('header', 'topbar'); header.append(element('span', 'crumb', 'VNDB Local · 个人视觉小说资料库')); workspace.prepend(header);
  }
  const main = workspace.querySelector('main'); main.id = 'main'; main.tabIndex = -1;
  if (!document.querySelector('.skip')) { const skip = element('a', 'skip', '跳到主要内容'); skip.href = '#main'; document.body.prepend(skip); }
  const dialog = element('dialog', 'mobile-navigation'); dialog.setAttribute('aria-label', '站点导航');
  const close = element('button', 'secondary', '关闭菜单'); close.type = 'button'; close.onclick = () => dialog.close();
  dialog.append(close, navigation(true)); document.body.append(dialog);
  const toggle = element('button', 'navigation-toggle'); toggle.type = 'button'; toggle.append(icon('menu'), element('span', '', '菜单'));
  toggle.setAttribute('aria-haspopup', 'dialog'); toggle.setAttribute('aria-expanded', 'false');
  toggle.onclick = () => { if (!dialog.open) dialog.showModal(); toggle.setAttribute('aria-expanded', 'true'); };
  dialog.addEventListener('close', () => toggle.setAttribute('aria-expanded', 'false'));
  workspace.querySelector('.topbar').prepend(toggle);
  function setView(value) {
    current = value;
    for (const a of document.querySelectorAll('[data-destination]')) {
      const selected = a.dataset.destination === current;
      a.classList.toggle('selected', selected);
      if (selected) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
      if (selected && a.closest('details')) a.closest('details').open = true;
    }
  }
  setView(current);
  return { setView };
})();
