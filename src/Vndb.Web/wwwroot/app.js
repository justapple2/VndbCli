"use strict";
const $ = id => document.getElementById(id);
const state = {mode:'local', query:'', producer:'', page:1, more:false, key:VnView.storage('sessionStorage').get('library-key')||'', importing:false};
const defaults = {query:'', producer:'', page:1, layout:'grid', density:'comfortable', fit:'contain', selected:'', scroll:0, year:'', status:'', played:'', unmatched:false, language:'', releaseType:'', platform:'', includeMtl:false, sort:'personal'};
const viewStores = Object.fromEntries(['local','remote','plays'].map(name=>[name,VnView.store(name,{...defaults,layout:name==='plays'&&VnView.storage('localStorage').get('vndb-play-view')==='list'?'list':'grid'})]));
const browseRead = VnView.latest(), detailRead = VnView.latest();
let producers = [], lastData = new Map(), displayedPages = new Map(), metadataTotals;
const node = VnView.element;
const searchHistory = VnView.historyControl($('search-history'),$('query'),'lookup');
const title = n => VnDetails.title(n,n.title).title;
let activeDetail=null,detailHistory=[];
VnDetails.preferencesControl($('title-preferences'),()=>{const cached=lastData.get(state.mode);if(cached&&state.mode!=='plays')renderBooks(cached);if(activeDetail)renderDetail(activeDetail);});
const company = p => p.original || p.name;
const date = value => new Date(value).toLocaleDateString('zh-CN',{year:'numeric',month:'2-digit',day:'2-digit'});
function notify(message,error=false){$('notice').textContent=message;$('notice').className='notice'+(error?' error':'');$('notice').hidden=!message;const d=$('detail-status');if(d){d.textContent=message;d.className='notice'+(error?' error':'');d.hidden=!message;}}
let toastTimer;
function toast(message){$('toast').textContent=message;$('toast').hidden=false;clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('toast').hidden=true,5500);}
async function api(path,{post=false,method,body,signal}={}){
 const response=await fetch(path,{method:method||(post?'POST':'GET'),body:body===undefined?undefined:JSON.stringify(body),headers:{...(body===undefined?{}:{'Content-Type':'application/json'}),'X-Library-Request':'1',...(state.key?{'X-Library-Key':state.key}:{})},signal:signal?AbortSignal.any([signal,AbortSignal.timeout(300000)]):AbortSignal.timeout(300000)});
 if(!response.ok){let message=`请求失败（${response.status}）`;try{message=(await response.json()).title||message;}catch{}const error=Object.assign(new Error(message),{status:response.status});if(response.status===401)openSettings();throw error;}
 return response.json();
}
function placeholder(){return node('div','cover-placeholder','暂无封面');}
function cover(entry){const wrap=node('div','cover-wrap');if(entry.cover){const img=node('img');img.src=`/api/library/${entry.novel.id}/cover?v=${encodeURIComponent(entry.updated_at)}`;img.alt=title(entry.novel)+' 封面';img.loading='lazy';img.onerror=()=>img.replaceWith(placeholder());wrap.append(img);}else wrap.append(placeholder());return wrap;}
function remember(){state.scroll=scrollY;viewStores[state.mode].save(state,{url:'/?view='+state.mode});}
function controls(){
 $('query').value=state.query;$('producer-select').value=state.producer;
 for(const key of ['layout','density','fit'])$('collection-'+key).value=state[key];
 for(const [id,key] of [['play-year','year'],['play-status','status'],['play-played','played']])$(id).value=state[key];
 $('play-unmatched').checked=state.unmatched;
 for(const key of ['language','releaseType','platform','sort'])$('public-'+key).value=state[key];$('public-includeMtl').checked=state.includeMtl;
 VnView.appearance($('content'),state);
 $('content').setAttribute('aria-label',state.mode==='plays'?'游玩记录':'作品资料');
 const filters=[state.query?'搜索：'+state.query:'',state.mode==='local'&&state.producer?'会社：'+(producers.find(p=>p.id===state.producer)?.original||state.producer):'',state.mode==='plays'?state.year:'',state.mode==='plays'&&state.status?$('play-status').selectedOptions[0]?.text:'',state.mode==='plays'&&state.played?($('play-played').selectedOptions[0]?.text):'',state.mode==='plays'&&state.unmatched?'待关联':''].filter(Boolean);
 if(state.mode==='local')for(const key of ['language','releaseType','platform'])if(state[key])filters.push($('public-'+key).selectedOptions[0]?.text||state[key]);if(state.mode==='local'&&state.includeMtl)filters.push('包含机翻');$('filter-summary').textContent=filters.join(' · ')||'未设置筛选';$('clear-filters').hidden=!filters.length;
}
function change(patch,explicit=false){remember();Object.assign(state,patch,{scroll:0});viewStores[state.mode].save(state,{push:true,url:'/?view='+state.mode});controls();load(true,explicit);}
function selectBook(id){state.selected=id;remember();VnView.select($('content'),id);}
async function mode(value,{push=true,rememberOld=true,patch,read=true}={}){
 if(rememberOld)remember();browseRead.cancel();$('retry-browse').hidden=true;state.mode=value;Object.assign(state,viewStores[value].read(),patch);
 viewStores[value].save(state,{push,url:'/?view='+value});controls();updateTabs();
 const cached=lastData.get(value);$('content').replaceChildren();if(cached){if(value==='plays')window.playJournal.render(cached.data,cached.summary);else renderBooks(cached);}if(read)await load(true);else empty('已回填作品ID','点击搜索后才会查询与补全。');
}
const producerRead=VnView.latest();let producerTimer;
async function producerOptions(){
 const ticket=producerRead.begin();try{const rows=await api('/api/v1/catalog/producers?'+new URLSearchParams({q:$('producer-search').value}),{signal:ticket.signal});if(!ticket.current())return;
 const previous=producers.find(p=>p.id===state.producer);producers=rows;if(previous&&!rows.some(p=>p.id===previous.id))producers.unshift(previous);
 $('producer-select').replaceChildren(new Option('所有开发会社',''));for(const p of producers)$('producer-select').append(new Option(`${company(p)} (${p.novel_count})`,p.id));
 if(state.producer&&!producers.some(p=>p.id===state.producer))$('producer-select').append(new Option(state.producer,state.producer));$('producer-select').value=state.producer;
 $('producer-nav').replaceChildren();for(const p of rows.slice(0,30)){const b=node('button','producer-link',company(p));b.title=p.name+' / '+p.id;b.onclick=()=>filter(p.id);$('producer-nav').append(b);}
 }catch(error){if(ticket.current())notify(VnView.failure(error),true);}
}
async function stats(){
 const s=await api('/api/stats');metadataTotals=s;showMetadataStats();$('side-count').textContent=s.novels;$('producer-count').textContent=s.producers;await producerOptions();
}
function showMetadataStats(){if(metadataTotals&&state.mode!=='plays'){const s=metadataTotals;for(const [id,value] of Object.entries({'stat-novels':s.novels,'stat-producers':s.producers,'stat-covers':s.covers,'stat-failures':s.cover_failures}))$(id).textContent=value;}}
function filter(id){if(state.mode!=='local')return mode('local',{patch:{producer:id,page:1,scroll:0}});change({producer:id,page:1});}
function updateTabs(){
 $('public-filters').hidden=state.mode!=='local';$('catalog-coverage').hidden=state.mode!=='local';
 for(const [id,value] of [['local-tab','local'],['plays-tab','plays'],['remote-tab','remote']])$(id).setAttribute('aria-pressed',String(state.mode===value));
 window.VnShell.setView(state.mode);if(state.mode==='plays'){window.playJournal?.updateTabs();return;}
 window.playJournal?.hide();showMetadataStats();$('local-tab').classList.toggle('active',state.mode==='local');$('remote-tab').classList.toggle('active',state.mode==='remote');
 $('producer-tools').hidden=state.mode==='remote';$('view-note').textContent='公共资料与个人书架分别浏览 · Web资料优先';
 $('query').placeholder=state.mode==='local'?'搜索标题、原文名或别名…':'输入标题或 VNDB ID…';
 $('results-label').textContent=state.mode==='remote'?'作品查询结果':'公共资料目录';$('crumb-view').textContent=state.mode==='remote'?'查询与收录':'公共资料目录';
}
function empty(heading,copy){const wrap=node('div','empty');wrap.append(node('h3','',heading),node('p','',copy));$('content').append(wrap);}
function renderBooks(data){
 $('content').replaceChildren();VnView.appearance($('content'),state);
 for(const entry of data.results){const n=entry.novel;const badges=[node('span','','资料已收录')];if(entry.catalog_personal){badges.push(node('span','',VnPublicCatalog.personal(entry.catalog_personal)));if(entry.catalog_personal.is_local)badges.push(node('span','local-presence','▣ 本地已有（客户端上报）'));badges.push(node('span','',VnPublicCatalog.source(entry.provenance)+' · 观测 '+entry.updated_at.slice(0,10)+(entry.completeness==='partial_text'?' · 文字部分覆盖':'')+(entry.releases_known?' · 有发行目录':' · 发行目录未知')));}else badges.push(window.localPresence.badge(n.id));if(n.rating)badges.push(node('span','','VNDB '+(n.rating/10).toFixed(1)));
  $('content').append(VnView.card({id:n.id,title:title(n),secondaryTitle:VnDetails.title(n,n.title).japanese,company:entry.producers.map(company).join(' / ')||'会社待补全',subtitle:n.id+' · '+(n.released||'发售日期未知'),url:entry.cover?'/api/library/'+n.id+'/cover?v='+encodeURIComponent(entry.updated_at):null,badges,open:()=>{selectBook(n.id);detail(n.id);}}));
 }
 if(data.coverage)$('catalog-coverage').textContent=VnPublicCatalog.coverage(data.coverage);
 VnView.select($('content'),state.selected);
 if(!data.results.length)empty('没有匹配的资料','可以清除筛选、尝试其他标题或明确查询 VNDB。');
 $('results-count').textContent=(data.source==='vndb'?`本页 ${data.results.length} 部 · 已保存到资料库`:`${data.total} 部 · 资料库结果`);
}
async function load(restore=false,explicit=false){
 if(state.mode==='plays'){await window.playJournal.load(restore);return;}
 const ticket=browseRead.begin(),view=state.mode,position=state.scroll;let succeeded=false;notify('');$('retry-browse').hidden=true;$('content').setAttribute('aria-busy','true');$('results-count').textContent='正在读取…';$('previous').disabled=true;$('next').disabled=true;
 try{
  if(view==='remote'&&!state.query){$('content').replaceChildren();empty('查询作品','输入标题或 VNDB ID，先查资料库，缺失时联网保存。');state.more=false;succeeded=true;displayedPages.set(view,1);$('results-count').textContent='等待输入';return;}
  const limit=state.query?12:24;
  let data=view==='local'?VnPublicCatalog.adapt(await api('/api/v1/catalog?'+VnPublicCatalog.parameters(state),{signal:ticket.signal}),state.page):await api('/api/search?'+new URLSearchParams({q:state.query,producer:view==='local'?state.producer:'',page:state.page,limit}),{signal:ticket.signal});
  if(view!=='local')await window.localPresence.load();if(!ticket.current())return;
  if(view==='remote'&&data.needs_remote&&state.query&&explicit){
   if(!state.key)throw new Error('资料库暂无匹配。联网查询并保存资料需要管理密钥。');
   notify('正在从 VNDB 查询并保存资料…');
   data=await api('/api/search',{post:true,body:{query:state.query,producer:view==='local'?state.producer||null:null,page:state.page,limit:12},signal:ticket.signal});
   if(!ticket.current())return;stats().catch(()=>{});
  }
  if(!ticket.current())return;notify(view==='remote'&&data.needs_remote&&!explicit?'本库暂无匹配；点击搜索后才会联网查询并收录。':'');state.more=data.more;renderBooks(data);lastData.set(view,data);displayedPages.set(view,state.page);succeeded=true;
  if(restore)VnView.restoreScroll(position);
 }catch(error){if(!ticket.current())return;notify(VnView.failure(error),true);$('retry-browse').hidden=false;$('results-count').textContent='读取失败 · 已显示的结果保留';if(!$('content').children.length)empty('暂时无法读取',VnView.failure(error));}
 finally{if(ticket.current()){$('content').setAttribute('aria-busy','false');$('previous').disabled=!succeeded||state.page===1;$('next').disabled=!succeeded||!state.more;$('page-label').textContent=succeeded?'第 '+state.page+' 页':displayedPages.has(view)?'上次成功结果 · 第 '+displayedPages.get(view)+' 页':'尚未取得结果';}}
}
async function importNovel(id,button,refresh=false){if(state.importing)return;state.importing=true;button.disabled=true;const old=button.textContent;button.textContent=refresh?'正在刷新…':'正在收录…';notify('正在获取完整资料、开发会社与封面，请稍候…');try{const entry=await api(`/api/imports/${id}?refresh=${refresh}`,{post:true});notify(entry.cover_status==='failed'?'文字资料已保存；封面下载失败，可在详情中刷新重试。':'');toast(`${title(entry.novel)} 已${refresh?'更新':'保存'}到资料库`);button.textContent='已收录 ✓';await stats();if(refresh&&$('detail-dialog').open&&$('detail-dialog').dataset.novelId===id){renderDetail(entry);notify(entry.cover_status==='failed'?'资料已更新，封面仍待重试。':'资料与封面已更新。');}}catch(error){notify(error.message,true);button.textContent=old;}finally{state.importing=false;button.disabled=false;}}
async function detail(id,related=false){
 if(!/^v[1-9][0-9]*$/.test(id))return;
 if(related&&detailHistory.length>=8){notify('关联导航最多8层，请先返回。',true);return;}
 const parent=activeDetail,ticket=detailRead.begin();
 try{
  const entry=await api('/api/library/'+id,{signal:ticket.signal});if(!ticket.current())return;
  if(entry?.novel?.id!==id)throw new Error('关联回复身份不符，原详情保留。');
  if(related&&parent)detailHistory.push(parent);else detailHistory=[];
  renderDetail(entry);if(!$('detail-dialog').open)$('detail-dialog').showModal();
 }catch(error){if(ticket.current()){
  notify(VnView.failure(error),true);
  const target=$('detail-status')||$('notice'),fill=node('button','secondary','回填 '+id+' 到查询，不执行');fill.type='button';
  fill.onclick=()=>{$('detail-dialog').close();mode('remote',{patch:{query:id,page:1,scroll:0},read:false});};target.append(fill);
 }}
}
function renderDetail(entry){
 activeDetail=entry;const n=entry.novel;$('detail-dialog').dataset.novelId=n.id;
 const current=()=>activeDetail===entry&&$('detail-dialog').open;
 const layout=node('div','detail-layout'),left=node('div','detail-cover');left.append(cover(entry));const info=node('div','detail-info');
 if(detailHistory.length){const back=node('button','secondary','返回原详情');back.onclick=()=>{detailRead.cancel();renderDetail(detailHistory.pop());};info.append(back);}
 const presented=VnDetails.title(n,n.title),heading=node('h2','',presented.title);heading.id='detail-title';info.append(node('div','eyebrow','PUBLIC METADATA / '+n.id),heading);
 if(presented.japanese)info.append(node('div','latin-title',presented.japanese));
 const chips=node('div','chips');for(const p of entry.producers||[]){const b=node('button','chip',company(p));b.title=p.name+' / '+p.id;b.onclick=()=>{$('detail-dialog').close();filter(p.id);};chips.append(b);}
 const feedback=node('div','notice');feedback.id='detail-status';feedback.hidden=true;info.append(chips,feedback);
 info.append(VnDetails.publicSection(n,VnPublicCatalog.source(entry.provenance),entry.updated_at,entry.producers||[],entry.completeness),VnReleases.section(n,api,current));
 if(n.description)info.append(node('h3','','公共作品简介'),node('div','description',n.description));
 info.append(VnDetails.relations(n,id=>detail(id,true),{known:id=>[...lastData.values()].flatMap(p=>p.results||[]).find(r=>r.novel?.id===id)?.novel}));
 if(n.titles?.length){info.append(node('h3','','其他语言标题'));const list=node('ul','titles-list');for(const t of n.titles.slice(0,30))list.append(node('li','',`${t.lang} / ${t.title}${t.main?' · 原始标题':''}`));info.append(list);}
 if(n.aliases?.length)info.append(node('h3','','别名'),node('div','description',n.aliases.slice(0,100).join(' / ')));
 const actions=node('div','detail-actions'),refresh=node('button','primary','从 VNDB 刷新');refresh.onclick=()=>importNovel(n.id,refresh,true);
 const download=node('a','secondary','导出 JSON');download.href='/api/library/'+n.id+'/export';download.download=n.id+'.json';
 const original=node('a','plain','在 VNDB 查看 ↗');original.href='https://vndb.org/'+n.id;original.target='_blank';original.rel='noopener noreferrer';
 actions.append(refresh,download,original);info.append(actions,node('div','detail-update',`首次收录 ${date(entry.created_at)} · 最近更新 ${date(entry.updated_at)} · ${entry.completeness==='partial_text'?'文字部分覆盖；缺失可显式刷新':'作品文字资料'} · 封面独立缓存`));
 layout.append(left,info);$('detail-content').replaceChildren(layout);const presence=window.localPresence.badge(n.id);if(presence)info.append(presence);window.playJournal?.forNovel(n,info);
}
function openSettings(){$('write-key').value=state.key;if(!$('settings-dialog').open)$('settings-dialog').showModal();}

$('search-form').onsubmit=event=>{event.preventDefault();searchHistory.record($('query').value);change({query:$('query').value.trim(),page:1},true);};
$('producer-select').onchange=()=>filter($('producer-select').value);$('producer-search').oninput=()=>{clearTimeout(producerTimer);producerTimer=setTimeout(producerOptions,350);};$('producer-search').onkeydown=event=>{if(event.key==='Enter')event.preventDefault();};
$('local-tab').onclick=()=>mode('local');$('all-nav').onclick=event=>{event.preventDefault();mode('local');};
for(const id of ['remote-tab','remote-nav','add-button'])$(id).onclick=event=>{event.preventDefault();mode('remote');};
$('previous').onclick=()=>{if(state.page>1)change({page:state.page-1},true);};$('next').onclick=()=>{if(state.more)change({page:state.page+1},true);};
$('retry-browse').onclick=()=>load(true);
for(const key of ['language','releaseType','platform','sort'])$('public-'+key).onchange=()=>change({[key]:$('public-'+key).value,page:1});$('public-includeMtl').onchange=()=>change({includeMtl:$('public-includeMtl').checked,page:1});
$('clear-filters').onclick=()=>change({query:'',producer:'',year:'',status:'',played:'',unmatched:false,language:'',releaseType:'',platform:'',includeMtl:false,sort:'personal',page:1});
for(const key of ['layout','density','fit'])$('collection-'+key).onchange=()=>{state[key]=$('collection-'+key).value;remember();VnView.appearance($('content'),state);};
for(const id of ['settings','settings-top'])$(id).onclick=openSettings;
$('detail-dialog').addEventListener('close',()=>{detailRead.cancel();activeDetail=null;detailHistory=[];});
$('close-settings').onclick=()=>$('settings-dialog').close();$('close-detail').onclick=()=>$('detail-dialog').close();
$('save-key').onclick=()=>{state.key=$('write-key').value.trim();VnView.storage('sessionStorage').set('library-key',state.key);$('settings-dialog').close();toast('管理密钥已保存到当前会话');};
$('clear-key').onclick=()=>{state.key='';VnView.storage('sessionStorage').remove('library-key');$('write-key').value='';toast('已清除当前会话密钥');};
for(const id of ['detail-dialog','settings-dialog'])$(id).addEventListener('click',event=>{if(event.target===$(id)){const r=$(id).getBoundingClientRect();if(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom)$(id).close();}});
let scrollTimer;window.addEventListener('scroll',()=>{clearTimeout(scrollTimer);scrollTimer=setTimeout(remember,180);},{passive:true});
window.addEventListener('pagehide',remember);
window.addEventListener('popstate',()=>mode(new URLSearchParams(location.search).get('view')==='plays'?'plays':new URLSearchParams(location.search).get('view')==='remote'?'remote':'local',{push:false,rememberOld:false}));
window.addEventListener('DOMContentLoaded',()=>{
 const params=new URLSearchParams(location.search),initial=params.get('view');
 mode(initial==='plays'?'plays':initial==='remote'?'remote':'local',{push:false,rememberOld:false});stats().catch(error=>notify(VnView.failure(error),true));
 const linked=params.get('vn');if(/^v[1-9][0-9]*$/.test(linked||''))detail(linked);
});
