'use strict';
globalThis.VnPublicCatalog = (() => {
  function parameters(state) {
    return new URLSearchParams({q:state.query||'',producer_id:state.producer||'',language:state.language||'',release_type:state.releaseType||'',platform:state.platform||'',include_mtl:String(!!state.includeMtl),sort:state.sort||'personal',page:state.page||1,page_size:24});
  }
  function personal(p) {
    if (!p || !p.linked_items) return '○ 未记录 · 未加入书架';
    return `关联 ${p.linked_items} 项 · ✓ 曾玩过 ${p.played} · ◷ 未玩 ${p.unplayed} · ○ 未记录 ${p.unrecorded}`+(p.needs_review?` · ! 待核对 ${p.needs_review}`:'');
  }
  const source = s => ({vndb_dump:'VNDB官方快照',web_vndb:'VNDB API',client_vndb_import:'客户端VNDB快照'}[s]||s||'来源未知');
  function coverage(c) {
    return `Web公共资料 ${c.public_novels} 部 · ${c.completed_snapshot_at?'已验证快照 '+c.completed_snapshot_at.slice(0,10):'尚无完整导入凭证'}`+
      (c.active_state?` · 导入 ${c.active_state} ${c.processed}/${c.total}`:'')+'；仅检索已收录资料，字段/封面可能缺失，不保证最新全站覆盖。语言/类型须命中同一发行版，含未来发行；不证明本机版本。';
  }
  function adapt(page, requestedPage = page?.page) {
    const integer=n=>Number.isSafeInteger(n)&&n>=0,c=page?.coverage,ids=new Set();
    if (!Array.isArray(page?.items) || !c || page.source!=='web_catalog' || !integer(page.total) ||
      !Number.isInteger(page.page) || page.page<1 || page.page!==requestedPage || page.page_size!==24 ||
      page.items.length!==Math.min(24,Math.max(0,page.total-(page.page-1)*24)) || page.more!==(page.page*24<page.total) ||
      !integer(c.public_novels) || c.public_novels<page.total || !integer(c.processed) || !integer(c.total) || c.processed>c.total ||
      !Number.isFinite(Date.parse(page.observed_at))) throw new SyntaxError('公共目录回复不完整，原结果保留。');
    for(const entry of page.items){
      const m=entry?.metadata,n=m?.novel,p=entry?.personal;
      if(!m||!n||!/^v[1-9][0-9]*$/.test(m.vndb_id)||n.id!==m.vndb_id||ids.has(n.id)||typeof n.title!=='string'||
        !Array.isArray(n.titles)||!n.titles.every(t=>t&&typeof t.title==='string'&&typeof t.lang==='string')||!Array.isArray(m.developers)||!m.developers.every(d=>d&&typeof d.name==='string')||
        !m.cover||!Number.isFinite(Date.parse(m.fetched_at))||!p||
        !['linked_items','played','unplayed','unrecorded','needs_review'].every(k=>integer(p[k]))||
        p.played+p.unplayed+p.unrecorded+p.needs_review!==p.linked_items||(p.is_local&&p.linked_items===0))
        throw new SyntaxError('公共目录条目无效，原结果保留。');
      ids.add(n.id);
    }
    return {source:'public_catalog',total:page.total,more:page.more,coverage:page.coverage,results:page.items.map(i=>({novel:i.metadata.novel,producers:i.metadata.developers,cover:i.metadata.cover.status==='ready',updated_at:i.metadata.fetched_at,provenance:i.metadata.provenance,completeness:i.metadata.completeness,catalog_personal:i.personal,releases_known:i.releases_known}))};
  }
  return {parameters, personal, source, coverage, adapt};
})();
if (typeof module !== 'undefined') module.exports = globalThis.VnPublicCatalog;
