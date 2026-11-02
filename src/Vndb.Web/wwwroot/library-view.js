'use strict';
globalThis.VnView = (() => {
  const fields = {
    query: v => typeof v === 'string' && v.length <= 200,
    producer: v => typeof v === 'string' && (v === '' || /^p[1-9][0-9]*$/.test(v)),
    page: v => Number.isSafeInteger(v) && v >= 1 && v <= 100000,
    layout: v => ['grid', 'list'].includes(v),
    density: v => ['comfortable', 'compact'].includes(v),
    fit: v => ['contain', 'cover'].includes(v),
    selected: v => typeof v === 'string' && /^[a-zA-Z0-9_-]{0,160}$/.test(v),
    scroll: v => Number.isFinite(v) && v >= 0 && v <= 10000000,
    year: v => typeof v === 'string' && (v === '' || /^\d{4}$/.test(v)),
    status: v => ['', 'not_started', 'playing', 'played', 'completed', 'on_hold', 'dropped'].includes(v),
    played: v => ['', 'true', 'false'].includes(v),
    unmatched: v => typeof v === 'boolean',
    progress: v => ['', 'unrecorded', 'unplayed', 'played', 'review'].includes(v),
    presence: v => ['', 'true', 'false'].includes(v),
    statusFilter: v => ['', 'not_started', 'playing', 'played', 'completed', 'on_hold', 'dropped'].includes(v),
    language: v => typeof v === 'string' && /^[a-zA-Z0-9-]{0,20}$/.test(v),
    releaseType: v => ['', 'complete', 'trial', 'partial', 'patch'].includes(v),
    platform: v => typeof v === 'string' && /^[a-zA-Z0-9-]{0,30}$/.test(v),
    includeMtl: v => typeof v === 'boolean',
    sort: v => ['personal', 'updated', 'title', 'released_desc', 'released_asc'].includes(v),
    tab: v => ['shelf', 'imports', 'review', 'trash'].includes(v)
  };
  function storage(kind, env = globalThis) {
    return {
      get(key) { try { return env[kind].getItem(key); } catch { return null; } },
      set(key, value) { try { env[kind].setItem(key, value); } catch {} },
      remove(key) { try { env[kind].removeItem(key); } catch {} }
    };
  }
  function store(scope, defaults, env = globalThis) {
    const key = 'vndb-view-' + scope, memory = storage('sessionStorage', env), preferences = storage('localStorage', env);
    function clean(input) {
      return Object.fromEntries(Object.entries(defaults).map(([k, value]) => [k, fields[k]?.(input?.[k]) ? input[k] : value]));
    }
    let current = clean(defaults);
    function read() {
      try {
        const raw = preferences.get(key), parsed = raw && raw.length <= 1024 ? JSON.parse(raw) : {};
        current = clean({...current, ...Object.fromEntries(['layout','density','fit'].filter(k => fields[k](parsed[k])).map(k => [k,parsed[k]]))});
      } catch {}
      try { const raw = memory.get(key); current = clean(raw && raw.length <= 8192 ? JSON.parse(raw) : current); } catch {}
      try { const saved = env.history.state?.vnViews?.[scope]; if (saved) current = clean(saved); } catch {}
      return {...current};
    }
    function save(patch, { push = false, url } = {}) {
      current = clean({...current, ...patch}); memory.set(key, JSON.stringify(current));
      preferences.set(key, JSON.stringify(Object.fromEntries(['layout','density','fit'].filter(k => k in current).map(k => [k,current[k]]))));
      try {
        const previous = env.history.state || {};
        env.history[push ? 'pushState' : 'replaceState']({...previous, vnViews: {...previous.vnViews, [scope]: current}}, '', url);
      } catch {} // Storage/history restrictions must not disable browsing.
      return {...current};
    }
    read(); return {read, save};
  }
  function latest() {
    let controller, revision = 0;
    return { begin() {
      controller?.abort(); controller = new AbortController(); const own = ++revision;
      return {signal: controller.signal, current: () => own === revision};
    }, cancel() { controller?.abort(); revision++; } };
  }
  function failure(error) {
    if (error instanceof SyntaxError) return '服务返回的数据格式无效，请检查版本或服务状态；已显示的资料仍保留。';
    if (error.status === 401 || error.status === 403) return '需要有效的管理密钥；浏览结果仍保留。';
    if (error.status === 409) return '资料已发生变化，请重新核对；当前内容仍保留。';
    if (error.status === 413) return '请求超过服务端限额，请缩小范围。';
    if (error.status === 429) return '查询过于频繁，请稍后重试。';
    if (error.name === 'TimeoutError' || error instanceof TypeError) return '暂时无法读取服务，请检查连接后重试；已显示的资料仍保留。';
    return error.message || '读取失败，请重试。';
  }
  function element(tag, cls, text) {
    const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e;
  }
  function cover(title, url, open) {
    const frame = element('button', 'vn-cover'); frame.type = 'button'; frame.setAttribute('aria-label', '查看：' + title); frame.onclick = open;
    const placeholder = element('span', 'vn-cover-placeholder'); placeholder.append(element('strong', '', 'VN'), element('span', '', url ? '封面加载中' : '暂无封面'));
    frame.append(placeholder); frame.dataset.state = url ? 'loading' : 'empty';
    if (url) {
      const img = element('img'); img.alt = title + ' 封面'; img.loading = 'lazy'; img.decoding = 'async';
      img.onload = () => { frame.dataset.state = 'ready'; placeholder.hidden = true; };
      img.onerror = () => { frame.dataset.state = 'failed'; img.remove(); placeholder.lastChild.textContent = '封面暂不可用'; };
      img.src = url; frame.append(img);
    }
    return frame;
  }
  function card({id, title, secondaryTitle, company, subtitle, url, open, badges = [], extra, actions = []}) {
    const article = element('article', 'vn-card'); article.dataset.itemId = id;
    const body = element('div', 'vn-card-body'), heading = element('h3', 'vn-title');
    const button = element('button', '', title); button.type = 'button'; button.title = title; button.onclick = open; heading.append(button);
    body.append(heading);
    if (secondaryTitle) { const secondary=element('p','vn-subtitle',secondaryTitle);secondary.title=secondaryTitle;body.append(secondary); }
    if (company) { const p = element('p', 'vn-company', company); p.title = company; body.append(p); }
    if (subtitle) body.append(element('p', 'vn-subtitle', subtitle));
    const flags = element('div', 'vn-badges'); flags.append(...badges.filter(Boolean)); body.append(flags);
    if (extra) body.append(extra);
    if (actions.length) {
      const bar = element('div', 'vn-actions');
      for (const action of actions) {
        const b = element('button', action.kind || 'secondary', action.label); b.type = 'button'; b.onclick = action.run; bar.append(b);
      }
      body.append(bar);
    }
    article.append(cover(title, url, open), body); return article;
  }
  function appearance(parent, state) {
    parent.className = 'vn-collection';
    for (const key of ['layout', 'density', 'fit']) parent.dataset[key] = state[key];
    select(parent, state.selected);
  }
  function select(parent, id) {
    for (const card of parent.querySelectorAll('[data-item-id]')) card.classList.toggle('chosen', card.dataset.itemId === id);
  }
  function restoreScroll(value) { requestAnimationFrame(() => requestAnimationFrame(() => scrollTo(0, value))); }
  function progressLabel(state) {
    const progress=state?.progress;
    if(!progress)return '状态未读取';
    if(progress.needs_review)return '关联待核对';
    if(progress.state==='unrecorded')return '未记录';
    const names={not_started:'未玩',playing:'游玩中',played:'玩过',completed:'已完成',on_hold:'搁置',dropped:'放弃'};
    return progress.statuses?.map(s=>names[s]||'待核对').join(' / ') || '状态未读取';
  }
  function itemBadges(state) {
    const badges=[element('span','vn-badge',progressLabel(state))];
    badges.push(element('span','vn-badge',state?.is_local?'在本地（客户端上报）':'Web存在：未标记已有'));
    return badges;
  }
  const shelfQuickViews = Object.freeze({all:{progress:'',presence:'',statusFilter:''},owned:{progress:'',presence:'true',statusFilter:''},
    owned_unplayed:{progress:'unplayed',presence:'true',statusFilter:''},playing:{progress:'',presence:'',statusFilter:'playing'},
    unrecorded:{progress:'unrecorded',presence:'',statusFilter:''},review:{progress:'review',presence:'',statusFilter:''}});
  function searchHistory(scope, env=globalThis) {
    if(!['lookup','catalog','shelf'].includes(scope))throw Error('Invalid history scope');
    const key='vndb-search-history-'+scope, saved=storage('localStorage',env);let values=[];
    const valid=q=>typeof q==='string'&&q.trim()===q&&q.length>0&&q.length<=200&&!/[\u0000-\u001f\u007f]/.test(q);
    function read(){try{const raw=saved.get(key);if(raw&&raw.length<=32768){const parsed=JSON.parse(raw);if(Array.isArray(parsed))values=[...new Map(parsed.filter(valid).map(q=>[q.toLowerCase(),q])).values()].slice(0,20);}}catch{}return [...values];}
    function save(next){values=next;saved.set(key,JSON.stringify(values));return [...values];}
    return {read,record(text){const q=text.trim();if(!valid(q))return read();return save([q,...read().filter(v=>v.toLowerCase()!==q.toLowerCase())].slice(0,20));},remove(q){return save(read().filter(v=>q!==null&&v!==q));}};
  }
  function historyControl(parent,input,scope){
    const history=searchHistory(scope),details=element('details','search-history'),heading=element('summary','','最近搜索（此站点，仅本浏览器）'),select=element('select');
    select.setAttribute('aria-label','本服务最近搜索');const bar=element('div','vn-history-actions');
    function render(){select.replaceChildren();for(const q of history.read()){const option=element('option','',q);option.value=q;select.append(option);}select.disabled=!select.children.length;}
    const fill=element('button','secondary','回填，不查询'),remove=element('button','plain','删除选中'),clear=element('button','plain','清空历史');
    for(const b of [fill,remove,clear])b.type='button';
    fill.onclick=()=>{if(!select.disabled){input.value=select.value;input.focus?.();}};
    remove.onclick=()=>{history.remove(select.value);render();};clear.onclick=()=>{history.remove(null);render();};
    bar.append(select,fill,remove,clear);details.append(heading,element('p','small','最多20项；仅显式提交时记录，回填不会联网。'),bar);parent.append(details);render();
    return {record(text){history.record(text);render();}};
  }
  return {storage, store, latest, failure, element, cover, card, appearance, select, restoreScroll, progressLabel, itemBadges,shelfQuickViews,searchHistory,historyControl};
})();
if (typeof module !== 'undefined') module.exports = globalThis.VnView;
