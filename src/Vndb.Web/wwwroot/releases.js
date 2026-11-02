'use strict';
globalThis.VnReleases = (() => {
  const language = code => ({ja:'日语',en:'英语','zh-Hans':'简体中文','zh-Hant':'繁体中文',ko:'韩语'}[code] || code);
  function summary(release, novelId) {
    const kind = release.vns?.find(v => v.id === novelId)?.rtype;
    return [(release.languages || []).map(l => language(l.lang)+(l.mtl?'（机翻）':'')).join(' / '),
      (release.platforms || []).join(' / '), release.official?'官方':'非官方', release.patch?'补丁':'',
      {trial:'试玩',partial:'部分',complete:'完整'}[kind] || '未知类型', '发行日期：'+(release.released || '未知')].filter(Boolean).join(' · ');
  }
  function section(novel, api, isCurrent=()=>true) {
    const node=VnView.element, root=node('section','release-catalog'), content=node('div');
    const button=node('button','secondary',novel.release_catalog?'刷新发行目录':'查询发行版本与语言');button.type='button';
    const status=node('p','caption');status.setAttribute('role','status');
    const filterLabel=node('label','release-filter','筛选已收录版本'),filter=node('input');
    filter.type='search';filter.placeholder='版本标题、语言或平台';filter.setAttribute('aria-label','筛选已收录发行版本');filterLabel.append(filter);
    const more=node('button','secondary');more.type='button';let shown=20,current;
    filter.oninput=()=>{shown=20;render(current);};more.onclick=()=>{shown+=20;render(current);};
    root.append(node('h3','','已收录发行版本'),node('p','caption','包含试玩、补丁和未来发行；不代表本机安装版本或当前可购买状态。'),filterLabel,content,more,button,status);
    function requireCatalog(catalog){
      const ids=new Set();
      if(!catalog||!Array.isArray(catalog.items)||catalog.items.length>1000||!Number.isFinite(Date.parse(catalog.fetched_at)))
        throw new Error('发行目录格式或作品关联无效，原始资料保留；请显式刷新。');
      for(const release of catalog.items){
        if(!/^r[1-9][0-9]*$/.test(release?.id)||ids.has(release.id)||typeof release.title!=='string'||!release.title.trim()||
          !Array.isArray(release.languages)||release.languages.some(l=>!l||typeof l.lang!=='string')||!Array.isArray(release.platforms)||
          !Array.isArray(release.vns)||!release.vns.some(v=>v?.id===novel.id&&['trial','partial','complete'].includes(v.rtype)))
          throw new Error('发行目录格式或作品关联无效，原始资料保留；请显式刷新。');
        ids.add(release.id);
      }
    }
    function render(catalog) {
      current=undefined;content.replaceChildren();filterLabel.hidden=!catalog;more.hidden=true;
      if(!catalog){content.append(node('p','','尚未查询发行目录。'));return;}
      try{requireCatalog(catalog);}catch(error){filterLabel.hidden=true;content.append(node('p','notice error',error.message));return;}
      current=catalog;
      const keyword=filter.value.trim().toLocaleLowerCase(),items=catalog.items.filter(r=>`${r.id} ${r.title} ${r.alttitle||''} ${summary(r,novel.id)}`.toLocaleLowerCase().includes(keyword));
      content.append(node('p','caption',`${catalog.items.length} 项 · 匹配 ${items.length} 项 · 显示 ${Math.min(shown,items.length)} 项 · 资料时间 ${new Date(catalog.fetched_at).toLocaleString('zh-CN')}`));
      if(!catalog.items.length)content.append(node('p','','本次查询没有返回发行记录。'));
      else if(!items.length)content.append(node('p','','没有匹配的已收录版本；可清空此处筛选。'));
      const list=node('ul','titles-list');for(const release of items.slice(0,shown)){const row=node('li');row.append(node('strong','',`${release.alttitle||release.title} · ${release.id}`),node('p','caption',summary(release,novel.id)));list.append(row);}content.append(list);
      more.hidden=items.length<=shown;more.textContent=`再显示 ${Math.min(20,Math.max(0,items.length-shown))} 项`;
    }
    render(novel.release_catalog);
    button.onclick=async()=>{
      button.disabled=true;status.textContent='正在查询发行目录，原资料保留…';
      try{
        const result=await api(`/api/v1/vns/${novel.id}/releases/${novel.release_catalog?'refresh':'resolve'}`,{post:true});
        if(!isCurrent())return;
        const matches=Array.isArray(result.items)?result.items.filter(i=>i?.vndb_id===novel.id):[];
        if(matches.length!==1||matches[0].novel?.id!==novel.id)throw new Error('发行回复作品身份不符，原资料保留。');
        const updated=matches[0].novel.release_catalog;requireCatalog(updated);
        novel.release_catalog=updated;render(updated);button.textContent='刷新发行目录';status.textContent='发行目录已保存，可离线查看。';
      }catch(error){if(isCurrent())status.textContent=VnView.failure(error);}finally{button.disabled=false;}
    };
    return root;
  }
  return {language,summary,section};
})();
if(typeof module!=='undefined')module.exports=globalThis.VnReleases;
