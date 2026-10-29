"use strict";
(() => {
 const labels={not_started:'未游玩',playing:'游玩中',played:'已游玩',completed:'本次完成',on_hold:'搁置',dropped:'放弃'};
 let lastNovel=null;
 const editor=VnPersonal.create({changed:async()=>{if(state.mode==='plays')await load();else{const summary=await api('/api/plays/stats');$('play-count').textContent=summary.total;}if(lastNovel?.parent.isConnected)await forNovel(lastNovel.novel,lastNovel.parent);}});
 const dates=d=>d.started_on||d.finished_on?`${d.started_on||'开始未知'} → ${d.finished_on||'结束未知'}`:'日期未知';
 const hours=d=>d.duration_minutes===null?'实际时长未知':`${(d.duration_minutes/60).toLocaleString('zh-CN',{maximumFractionDigits:2})} 小时`;
 function updateTabs(){
  window.VnShell.setView('plays');$('plays-tab').classList.add('active');$('local-tab').classList.remove('active');$('remote-tab').classList.remove('active');
  $('play-filters').hidden=false;$('producer-tools').hidden=true;
  $('page-title').textContent='我的游玩记录';$('subtitle').textContent='一次游玩一条记录，重玩分别保留。';
  $('add-button').textContent='选择作品后新增记录';$('view-note').textContent='个人记录 · 保存在 Web';$('results-label').textContent='游玩记录';$('crumb-view').textContent='游玩记录';$('query').placeholder='搜索作品标题、评价或备注…';
  ['记录数','已玩次数','已评分','待关联作品'].forEach((text,index)=>document.querySelectorAll('.statistics div span')[index].textContent=text);
 }
 function hide(){
  $('play-filters').hidden=true;$('plays-tab').classList.remove('active');
  $('page-title').textContent='视觉小说资料库';$('subtitle').textContent='作品资料与个人书架分别管理。';$('add-button').textContent='收录新作品';
  ['已收录资料','开发会社','已保存封面','封面待重试'].forEach((text,index)=>document.querySelectorAll('.statistics div span')[index].textContent=text);
 }
 function show(){return mode('plays');}
 function render(data,summary){
$('content').replaceChildren();state.more=data.more;
   for(const [id,value] of Object.entries({'stat-novels':summary.total,'stat-producers':summary.played,'stat-covers':summary.rated,'stat-failures':summary.unmatched,'play-count':summary.total}))$(id).textContent=value;
   $('play-year').replaceChildren(new Option('全部年份',''));summary.years.forEach(y=>$('play-year').append(new Option(y+' 年',y)));
   if(state.year&&!summary.years.map(String).includes(state.year))$('play-year').append(new Option(state.year+' 年',state.year));
   for(const record of data.results)$('content').append(card(record));controls();
   $('results-count').textContent=`${data.total} 条记录 · 重玩分别保留`;
   if(!data.results.length)empty('没有匹配的游玩记录','可以清除筛选或新增一次游玩。');
 }
 async function load(restore=false){
  const ticket=browseRead.begin(),position=state.scroll;updateTabs();notify('');$('content').setAttribute('aria-busy','true');
  $('previous').disabled=true;$('next').disabled=true;$('results-count').textContent='正在读取游玩记录…';
  const params=new URLSearchParams({q:state.query,page:state.page,limit:24});
  for(const key of ['year','status','played'])if(state[key])params.set(key,state[key]);
  if(state.unmatched)params.set('unmatched','true');
  try{
   const [data,summary]=await Promise.all([api('/api/plays?'+params,{signal:ticket.signal}),api('/api/plays/stats',{signal:ticket.signal}),window.localPresence.load()]);
   if(!ticket.current())return;lastData.set('plays',{data,summary});render(data,summary);
   if(restore)VnView.restoreScroll(position);
  }catch(error){if(ticket.current()){notify(VnView.failure(error),true);$('results-count').textContent='读取失败 · 已显示的结果保留';if(!$('content').children.length)empty('暂时无法读取',VnView.failure(error));}}
  finally{if(ticket.current()){$('content').setAttribute('aria-busy','false');$('previous').disabled=state.page===1;$('next').disabled=!state.more;$('page-label').textContent='第 '+state.page+' 页';}}
 }
 function card(record){
  const d=record.details;
  const t=record.time,time=t?(t.total_seconds===null?`历史未知 · 会话 ${t.session_seconds} 秒`:`${(t.total_seconds/3600).toFixed(2)} 小时`)+(t.pending_sessions?` · ${t.pending_sessions} 条待核对`:''):hours(d);
  const extra=node('p','vn-excerpt',d.review||'');
  return VnView.card({id:record.id,title:d.title,subtitle:dates(d)+' · '+time,url:d.novel_id?'/api/library/'+encodeURIComponent(d.novel_id)+'/cover':null,
   badges:[node('span','',labels[d.status]||'状态待核对'),node('span','',d.score===null?'未评分':`个人 ${d.score.toFixed(1)}/10`),window.localPresence.badge(d.novel_id)],extra,
   open:()=>{selectBook(record.id);d.novel_id?detail(d.novel_id):openEditor(record);},actions:[{label:'编辑记录',run:()=>{selectBook(record.id);openEditor(record);}}]});
 }
  async function openEditor(record=null){
    if(!record){location.assign('/manage.html');return;}
    try{await editor.open(record);}catch(error){notify(error.message,true);}
  }
  async function forNovel(novel,parent){
    lastNovel={novel,parent};parent.querySelector('.personal-history')?.remove();
    const section=node('section','personal-history');section.append(node('h3','','个人书架与游玩经历'));parent.append(section);
    const help=node('p','form-help','按个人条目分别展示，多版本与重玩不合并。');section.append(help);
    try{
      const items=await VnPersonal.itemsForNovel(novel.id);if(!section.isConnected)return;
      const link=node('a','secondary','在我的书架选择／新增版本');link.href='/manage.html';section.append(link);
      if(!items.length){section.append(node('p','form-help','尚无关联的个人书架条目；不能据此判断是否玩过。历史未关联记录仍可在游玩记录页编辑。'));return;}
      for(const view of items){
        const item=view.item,group=node('section','personal-edition');group.append(node('h4','',item.details.title_override||item.details.title),node('p','small','条目 '+item.id));section.append(group);
        VnPersonal.action(group,'新建这版的游玩／重玩',()=>editor.open(null,item));
        try{const records=await VnPersonal.request('/plays?item_id='+encodeURIComponent(item.id));if(!group.isConnected)return;
          if(records.some(r=>r.item_id!==item.id||r.deleted_at))throw Error('记录归属不符，暂不显示为本条目的经历。');
          for(const record of records)group.append(VnPersonal.playCard(record,r=>editor.open(r,item)));
          if(!records.length)group.append(node('p','form-help','此条目尚未记录游玩经历。'));
        }catch(error){group.append(node('p','notice error',error.message));}
      }
    }catch(error){section.append(node('p','notice error',error.message));}
  }
  $('plays-tab').onclick=show;$('plays-nav').onclick=event=>{event.preventDefault();show();};$('new-play').onclick=()=>openEditor();
  for(const [id,key] of [['play-year','year'],['play-status','status'],['play-played','played'],['play-unmatched','unmatched']])$(id).onchange=()=>change({[key]:key==='unmatched'?$(id).checked:$(id).value,page:1});
  const addBook=$('add-button').onclick;$('add-button').onclick=event=>state.mode==='plays'?openEditor():addBook(event);
  window.playJournal={load,render,hide,show,updateTabs,forNovel,openEditor};
  api('/api/plays/stats').then(s=>$('play-count').textContent=s.total).catch(()=>{});
})();
