"use strict";
(() => {
 const $=id=>document.getElementById(id),node=(tag,text,cls)=>VnView.element(tag,cls,text);
 const stored=VnView.store('shelf',{query:'',page:1,layout:VnView.storage('localStorage').get('vndb-manager-view')==='list'?'list':'grid',density:'comfortable',fit:'contain',selected:'',scroll:0,unmatched:false,progress:'',presence:'',statusFilter:'',favorite:'',label:'',tab:'shelf'});
 const searchHistory=VnView.historyControl($('item-history'),$('item-query'),'shelf');
 VnDetails.preferencesControl($('title-preferences'),()=>{if(lastBooks)renderBooks(lastBooks);if(book)structuredDetails(book);});
 let display=stored.read(),key=VnView.storage('sessionStorage').get('library-key')||'',tab='shelf',page=display.page,book=null,limits=null,lastBooks=null;
 let itemAttempt=null,itemInitial='',itemRejected=null;
 const reads=VnView.latest(),keys=new Map();
 const operation=()=>globalThis.crypto?.randomUUID?.()||Date.now().toString(36)+'-'+Math.random().toString(36).slice(2);
 function notice(text,error=false){$('message').textContent=text;$('message').hidden=!text;$('message').className='notice'+(error?' error':'');}
 function feedback(id,error){$(id).textContent=error;$(id).hidden=!error;}
 async function api(path,method='GET',body,signal,operationId){
  const signature=method+path+JSON.stringify(body),headers={};
  if(method!=='GET'){if(!keys.has(signature))keys.set(signature,operation());Object.assign(headers,{'X-Library-Request':'1','X-Library-Key':key,'Idempotency-Key':operationId||keys.get(signature)});}
  if(body!==undefined)headers['Content-Type']='application/json';
  const response=await fetch('/api/v1'+path,{method,headers,body:body===undefined?undefined:JSON.stringify(body),signal:signal?AbortSignal.any([signal,AbortSignal.timeout(300000)]):AbortSignal.timeout(300000)});
  const data=await response.json().catch(()=>null);
  if(!data)throw Error('返回数据无效，尚未确认操作结果。');
  if(!response.ok){if(response.status===401&&!$('key-dialog').open)$('key-dialog').showModal();throw Object.assign(new Error(data.title||'请求失败 '+response.status),{status:response.status});}
  keys.delete(signature);return data;
 }
 const organizer=globalThis.VnOrganization?.create({api:(path,options)=>api(path,options.method,options.body,undefined,options.operationId),changed:mutated=>{controls();if(mutated&&lastBooks)loadBooks();}});
 organizer?.mountManager($('personal-organization-tools'));
 window.addEventListener('beforeunload',event=>{if(organizer?.unsettled){event.preventDefault();event.returnValue='';}});
 let organizationLoaded=false;
 function action(parent,text,run,kind='secondary'){
  const b=node('button',text,kind);b.type='button';b.onclick=async()=>{b.disabled=true;try{await run();}catch(e){notice(VnView.failure(e),true);}finally{b.disabled=false;}};parent.append(b);return b;
 }
 function empty(parent,text){parent.append(node('p',text,'manager-empty'));}
 function persist(push=false){display.page=page;display.tab=tab;stored.save(display,{push,url:'/manage.html?tab='+tab});}
 function remember(){display.scroll=scrollY;persist();}
 function controls(){
  $('item-progress').value=display.progress;$('item-presence').value=display.presence;
  $('item-status').value=display.statusFilter;
  if($('favorite-filter'))$('favorite-filter').value=display.favorite||'';if($('personal-label-filter'))organizer?.filter($('personal-label-filter'),display.label);
  $('item-quick').value=Object.keys(VnView.shelfQuickViews).find(k=>Object.entries(VnView.shelfQuickViews[k]).every(([name,value])=>display[name]===value))||'custom';
  $('item-query').value=display.query;$('item-unmatched').checked=display.unmatched;
  for(const name of ['layout','density','fit'])$('collection-'+name).value=display[name];
  VnView.appearance($('items'),display);
  $('filter-summary').textContent=[display.query?'搜索：'+display.query:'',display.unmatched?'待关联 VNDB':'',display.progress?$('item-progress').selectedOptions?.[0]?.text:'',display.presence?$('item-presence').selectedOptions?.[0]?.text:'',display.statusFilter?$('item-status').selectedOptions?.[0]?.text:'',display.favorite==='true'?'★ 仅收藏':display.favorite==='false'?'未收藏':'',display.label?'分类：'+(organizer?.state.labels.find(l=>l.id===display.label)?.name||'已移除的分类'):''].filter(Boolean).join(' · ')||'未设置筛选';$('clear-filters').hidden=!display.query&&!display.unmatched&&!display.progress&&!display.presence&&!display.statusFilter&&!display.favorite&&!display.label;
 }
 function selected(item){display.selected=item.id;remember();VnView.select($('items'),item.id);}
 function change(patch){remember();Object.assign(display,patch,{scroll:0});page=1;persist(true);controls();loadBooks(true);}
 async function show(next,{push=true,rememberOld=true}={}){
  if(rememberOld)remember();reads.cancel();toolsRead.cancel();tab=next;display.tab=tab;persist(push);window.VnShell.setView(next==='shelf'?'shelf':next);notice('');
  for(const name of ['shelf','imports','review','trash'])$(name+'-panel').hidden=name!==tab;
  document.querySelectorAll('[data-tab]').forEach(b=>{b.classList.toggle('active',b.dataset.tab===tab);b.setAttribute('aria-pressed',String(b.dataset.tab===tab));});
  if(tab==='shelf')await loadBooks(true);if(tab==='imports')await loadImports();if(tab==='review')await loadCandidates();if(tab==='trash')await loadRemoved();
 }
 function renderBooks(data){
  $('items').replaceChildren();VnView.appearance($('items'),display);
  for(const view of data.items){const item=view.item,d=item.details;
   const presented=VnDetails.title(view.metadata?.novel,view.metadata?.title||d.title,d.title_override),title=presented.title;
   const card=VnView.card({id:item.id,title,secondaryTitle:presented.japanese,company:VnView.personalCompany(view),subtitle:(d.novel_id||'待关联 VNDB')+' · '+view.play_count+' 条游玩记录',url:view.metadata?.cover?.url||null,
    badges:[...VnView.itemBadges(view.state),organizer?.badge(item.id)].filter(Boolean),open:()=>openItem(item),actions:[{label:'编辑／游玩',run:()=>openItem(item)}]});
   const bar=card.querySelector('.vn-actions');action(bar,'移除',async()=>{if(confirm('移除此书架条目？可恢复，游玩记录和游戏文件保留。')){await api('/items/'+item.id+'?expected_revision='+item.revision,'DELETE');await loadBooks();}},'plain');$('items').append(card);
  }
  VnView.select($('items'),display.selected);
  if(!data.items.length)empty($('items'),display.query||display.unmatched||display.progress||display.presence?'没有匹配的条目，可以清除筛选。':'暂无个人条目，可新增作品或导入 JSON 并选择加入书架。');
  $('item-count').textContent=data.total+' 个个人条目 · 公共资料不自动加入个人书架';$('items-page').textContent='第 '+page+' 页';
 }
 async function openItem(item){selected(item);try{await editItem(item);}catch(error){notice(VnView.failure(error),true);}}
 async function loadBooks(restore=false){
  const ticket=reads.begin(),position=display.scroll;notice('');$('items').setAttribute('aria-busy','true');$('items-prev').disabled=true;$('items-next').disabled=true;
  try{
   if(organizer&&!organizationLoaded){try{await organizer.refresh();organizationLoaded=true;}catch(error){notice('分类未能读取，已有筛选与结果保留。'+error.message,true);}}
   const data=await api('/shelf?'+new URLSearchParams({q:display.query,unmatched:display.unmatched,progress:display.progress,status:display.statusFilter,...(display.favorite?{favorite:display.favorite}:{}),...(display.label?{label:display.label}:{}),...(display.presence?{is_local:display.presence}:{}),page,page_size:24}),'GET',undefined,ticket.signal);
   if(data.format!=='vn-shelf-summary'||data.format_version!==1||!Array.isArray(data.items)||data.items.some(v=>!v?.item?.id||!v.state?.progress))throw Error('书架摘要格式无效，保留最近结果。');
   if(!ticket.current()||tab!=='shelf')return;lastBooks=data;renderBooks(data);if(restore)VnView.restoreScroll(position);
  }catch(e){if(ticket.current()){notice(VnView.failure(e),true);$('item-count').textContent='读取失败 · 已显示的结果保留';if(!$('items').children.length)empty($('items'),'暂时无法读取，请检查连接后重新查找。');}}
  finally{if(ticket.current()){$('items').setAttribute('aria-busy','false');$('items-prev').disabled=page===1;$('items-next').disabled=!lastBooks?.more;}}
 }
  function structuredDetails(item){
    const parent=$('managed-details');parent.replaceChildren();if(!item)return;
    const view=detailView?.item.id===item.id?detailView:lastBooks?.items.find(v=>v.item.id===item.id),metadata=view?.metadata,novel=metadata?.novel;
    const presented=VnDetails.title(novel,metadata?.title||item.details.title,item.details.title_override);parent.append(node('h2',presented.title));if(presented.japanese)parent.append(node('p',presented.japanese,'small'));
    const layout=node('div',undefined,'managed-detail-header');
    if(metadata?.cover?.url){const image=node('img');image.src=metadata.cover.url;image.alt='作品封面';image.onerror=()=>image.replaceWith(node('span','暂无封面','small'));layout.append(image);}
    const info=node('div');info.append(node('p',VnView.progressLabel(view?.state),'personal-context'));info.append(node('p',VnView.personalCompany({item,metadata})),node('p','发行：'+(novel?.released||'未知'),'small'),node('p','条目 '+item.id+' · 修订 '+item.revision,'personal-context'));
    if(novel){const owner=itemRead;info.append(VnDetails.publicSection(novel,metadata?.provenance,metadata?.fetched_at,metadata?.developers||[],metadata?.completeness),VnReleases.section(novel,(path,options)=>api(path.replace('/api/v1',''),options?.post?'POST':'GET',options?.body,options?.signal),()=>itemRead===owner&&book?.id===item.id&&$('item-dialog').open),VnDetails.relations(novel,null,{links:true}));}
    if(novel?.description){const description=node('details');description.append(node('summary','作品简介'),node('p',novel.description,'personal-review'));info.append(description);}
    if(item.details.novel_id){const link=node('a','完整作品资料 ↗','plain');link.href='/?vn='+encodeURIComponent(item.details.novel_id);info.append(link);}
    info.append(node('h3','个人覆盖与记录'),node('p','个人标题覆盖：'+(item.details.title_override||'未覆盖'),'small'),node('p','个人会社覆盖：'+(item.details.company_override||'未覆盖'),'small'));
    info.append(node('p','存在状态来自客户端同步，具体位置与启动配置仅在本机客户端。','small'));layout.append(info);parent.append(layout);
  }
  function itemDraft(){return Object.fromEntries(['item-title','item-company','item-title-override','item-company-override','item-notes','item-extra'].map(id=>[id,$(id).value]));}
  let itemRead=0,detailView=null;
  async function editItem(item=null){
    if(itemAttempt){if(!$('item-dialog').open)$('item-dialog').showModal();feedback('item-feedback','前次条目修改尚未确认，请重试原操作。');return;}

    const ticket=++itemRead;
    if(item){const full=await api('/items/'+encodeURIComponent(item.id));if(ticket!==itemRead||itemAttempt)return;if(full.item.id!==item.id||full.item.deleted_at)throw Error('条目身份或移除状态变化，请刷新。');detailView=full;item=full.item;}else detailView=null;
    book=item;itemRejected=null;if($('item-comparison'))$('item-comparison').replaceChildren();organizer?.mountEditor($('personal-membership'),item?.id);structuredDetails(item);const d=item?.details||{};$('item-editor-title').textContent=item?'编辑个人条目':'新增作品（可暂不关联 VNDB）';
    for(const [id,value] of Object.entries({'item-title':d.title,'item-company':d.company_hint,'item-title-override':d.title_override,'item-company-override':d.company_override,'item-notes':d.notes,'item-extra':d.extra_notes}))$(id).value=value||'';
    $('item-fields').open=!item;$('item-inputs').disabled=false;$('item-reload').hidden=true;$('item-save').textContent='保存个人条目';
    $('item-vn').textContent='关联：'+(d.novel_id||'尚未关联');$('link-query').value=d.title||'';$('item-candidates').replaceChildren();$('item-plays').replaceChildren();feedback('item-feedback','');itemInitial=JSON.stringify(itemDraft());
    if(!$('item-dialog').open)$('item-dialog').showModal();
    if(item)await loadPlays();else $('item-plays').append(node('p','保存条目后可以添加本次游玩。','small'));
  }
  const playEditor=VnPersonal.create({changed:async()=>{if(book)await loadPlays();await loadBooks();}});
  let detailGeneration=0;
  async function loadPlays(){
    const generation=++detailGeneration,item=book,parent=$('item-plays');parent.replaceChildren(node('h3','游玩经历'));
    parent.append(node('p','条目 '+item.id+' · 修订 '+item.revision,'personal-context'));
    action(parent,'新建此条目的游玩／重玩',()=>playEditor.open(null,item));
    try{
      const rows=await api('/plays?item_id='+encodeURIComponent(item.id));
      if(generation!==detailGeneration||book?.id!==item.id)return;
      if(rows.some(r=>r.item_id!==item.id||r.deleted_at))throw Error('游玩记录归属不符，请重新读取。');
      for(const record of rows)parent.append(VnPersonal.playCard(record,r=>playEditor.open(r,item)));
      if(!rows.length)empty(parent,'此条目尚未记录游玩经历，未知时间不按零计算。');
    }catch(e){if(generation===detailGeneration)parent.append(node('p',e.message,'notice error'));}
  }
  $('item-form').onsubmit=async event=>{
    event.preventDefault();$('item-save').disabled=true;feedback('item-feedback','');
    try{
      if(!itemAttempt){const original=book?.details||{},keep=(id,name)=>$(id).value===(typeof original[name]==='string'?original[name].replace(/\r\n?/g,'\n'):original[name]??'')?original[name]??null:$(id).value||null;const details={title:$('item-title').value.trim(),novel_id:original.novel_id||null,company_hint:keep('item-company','company_hint'),title_override:keep('item-title-override','title_override'),company_override:keep('item-company-override','company_override'),notes:keep('item-notes','notes'),extra_notes:keep('item-extra','extra_notes')};
        itemAttempt={path:'/items'+(book?'/'+book.id:''),method:book?'PATCH':'POST',body:{details,expected_revision:book?.revision||null},id:operation()};}
      $('item-inputs').disabled=true;const attempt=itemAttempt;
      const saved=await api(attempt.path,attempt.method,attempt.body,undefined,attempt.id);
      if(!saved.id||!saved.details||book&&saved.id!==book.id||saved.revision!==(attempt.body.expected_revision||0)+1)throw Error('条目保存结果不符，尚未确认。');
      itemAttempt=null;book=saved;await editItem(book);await loadBooks();
    }catch(e){
      if(e.status>=400&&e.status<500&&![408,429].includes(e.status)){if(itemAttempt){itemRejected=structuredClone(itemAttempt);$('item-originals').querySelector('pre').textContent+='\n'+JSON.stringify(itemRejected,null,2);$('item-originals').hidden=false;}itemAttempt=null;$('item-inputs').disabled=false;}
      $('item-reload').hidden=e.status!==409||!book;
      feedback('item-feedback',e.message+(itemAttempt?'；结果尚未确认，字段已锁定，请重试原操作。':'；当前原稿保留，冲突时请明确重新读取。'));
      $('item-fields').open=true;$('item-save').textContent=itemAttempt?'重试原条目修改':'保存个人条目';
    }finally{$('item-save').disabled=false;}
  };
  if($('item-compare'))$('item-compare').onclick=async()=>{
    const original=book,attempt=itemAttempt||itemRejected;
    if(!original){feedback('item-feedback','请先选择已保存的个人条目；未创建的操作不能凭相似标题判断结果。');return;}
    try{
      const latest=await api('/items/'+encodeURIComponent(original.id));
      if(book!==original||attempt!==(itemAttempt||itemRejected)||!$('item-dialog').open)return;
      if(latest.item?.id!==original.id)throw Error('Web对照条目身份不符。');
      const d=original.details,keep=(id,name)=>$(id).value===(typeof d[name]==='string'?d[name].replace(/\r\n?/g,'\n'):d[name]??'')?d[name]??null:$(id).value||null;
      const incoming=attempt?.body.details||{...d,title:$('item-title').value.trim(),company_hint:keep('item-company','company_hint'),title_override:keep('item-title-override','title_override'),company_override:keep('item-company-override','company_override'),notes:keep('item-notes','notes'),extra_notes:keep('item-extra','extra_notes')};
      globalThis.VnDifferences?.render($('item-comparison'),VnDifferences.item(original.details,latest.item.details,incoming,{currentKnown:true}),'条目字段对照 · 基准 '+original.revision+' / Web '+latest.item.revision);
      feedback('item-feedback','只读观测；原操作ID和字段保持。'+(latest.item.deleted_at?'条目已移除，请回书架核对。':''));
    }catch(error){feedback('item-feedback',error.message+'；原稿和已显示对照保留。');}
  };
  $('item-reload').onclick=async()=>{
    if(!book||itemAttempt)return;const retained={item_id:book.id,expected_revision:book.revision,fields:itemDraft()};
    try{const latest=await api('/items/'+encodeURIComponent(book.id));if(latest.item?.id!==book.id)throw Error('条目读取结果不符。');
      $('item-originals').querySelector('pre').textContent+='\n'+JSON.stringify(retained,null,2);$('item-originals').hidden=false;await editItem(latest.item);$('item-fields').open=true;
    }catch(e){feedback('item-feedback',e.message);}
  };
  $('find-item-vn').onclick=async()=>{feedback('item-feedback','');$('item-candidates').replaceChildren();try{if(!book)throw new Error('请先保存个人条目，再核对关联。');const query=$('link-query').value.trim();if(!query)throw new Error('请输入标题或 VNDB ID。');const result=/^v[1-9][0-9]*$/.test(query)?await api('/vns/'+query+'/resolve','POST'):await api('/queries/vns','POST',{query,page_size:10});for(const match of result.items){const row=node('div',undefined,'manager-block');row.append(node('strong',match.title),node('p',match.vndb_id+' · '+match.developers.map(p=>p.original||p.name).join(' / '),'small'));action(row,'确认关联此作品',async()=>{book=await api('/items/'+book.id+'/link','POST',{novel_id:match.vndb_id,expected_revision:book.revision});await editItem(book);await loadBooks();});$('item-candidates').append(row);}if(!result.items.length)empty($('item-candidates'),'暂无候选；可以保持未关联。');}catch(e){feedback('item-feedback',e.message);}};
  const toolsRead=VnTools.latest(), toolWrites=VnTools.writes((path,options)=>VnTools.request('/api/v1'+path,options,key));
  let importsInFlight=0;
  function toolAction(parent,label,slot,path,body,refresh,validate){return VnTools.action(parent,{label,slot,path,body,refresh,validate},toolWrites);}
  function explicitToolReload(load){toolWrites.reload();$('tool-history').replaceChildren();if(toolWrites.archived.length)VnTools.details($('tool-history'),'重读前保留的原操作',toolWrites.archived.map(e=>({id:e.id,path:e.path,body:JSON.parse(e.json),state:e.state})));return load();}
  window.addEventListener('beforeunload',e=>{if(toolWrites.unsettled){e.preventDefault();e.returnValue='';}});
  function batchCard(batch,preview=false){
    const card=node('article',undefined,'manager-block');card.append(node('h3',(preview?'文件预览':'导入批次')+' · '+VnTools.label(batch.state)),node('p',(batch.accepted?'Web已接收；':'尚未提交；')+'处理 '+batch.completed+' / '+batch.total+' 项。处理结束仍可能含候选或失败。','small'));
    const rows=node('details');rows.append(node('summary','逐项结果（'+batch.items.length+'）'));
    for(const item of batch.items){const row=node('div',undefined,'tool-row');row.append(node('strong',(item.index+1)+'. '+item.title),node('p',VnTools.label(item.status),'tool-state'));if(item.message)row.append(node('p',item.message,'small'));rows.append(row);}card.append(rows);VnTools.details(card,'批次ID、摘要与原回执',batch);
    const valid=r=>r.id===batch.id&&Array.isArray(r.items)&&r.total===batch.total;
    if(preview&&batch.state==='preview')toolAction(card,'确认提交此批次','batch/'+batch.id,'/imports/'+batch.id+'/commit',null,loadImports,valid);
    if(['queued','processing'].includes(batch.state))toolAction(card,'暂停导入','batch/'+batch.id,'/imports/'+batch.id+'/pause',null,loadImports,valid);
    if(batch.state==='paused')toolAction(card,'继续导入','batch/'+batch.id,'/imports/'+batch.id+'/resume',null,loadImports,valid);
    return card;
  }
  $('preview-files').onclick=async()=>{
    notice('');$('preview-files').disabled=true;const files=Array.from($('json-files').files),addToLibrary=$('import-shelf').checked;
    try{
      if(!files.length)throw Error('先选择JSON文件。');
      for(const file of files){
        const block=node('article',undefined,'manager-block');block.append(node('h3',file.name));$('previews').append(block);
        try{limits=VnImport.limits(await api('/info'));const payload=await VnImport.read(file,'metadata',limits);
          toolAction(block,'生成只读预览','preview/'+operation(),'/imports/preview',{payload,add_to_library:addToLibrary},receipt=>{block.append(batchCard(receipt,true));},r=>typeof r.id==='string'&&Array.isArray(r.items)&&r.total===r.items.length);
          await block.querySelector('button').onclick();
        }catch(e){block.append(node('p',VnTools.failure(e),'notice error'));}
      }
    }catch(e){notice(VnTools.failure(e),true);}finally{$('preview-files').disabled=false;}
  };
  async function loadImports(){
    importsInFlight++;
    const current=toolsRead.next();try{
      const [batches,jobs]=await Promise.all([api('/imports'),api('/metadata/jobs')]);if(!current()||tab!=='imports')return;
      const cards=document.createDocumentFragment(),jobCards=document.createDocumentFragment();
      for(const batch of batches)cards.append(batchCard(batch));if(!batches.length)empty(cards,'尚无导入批次。');
      for(const job of jobs){const card=node('article',undefined,'manager-block');card.append(node('h3',job.title),node('p',VnTools.label(job.state),'tool-state'),node('p','尝试 '+job.attempts+(job.error?' · '+job.error:''),'small'));VnTools.details(card,'任务及候选证明',job);
        for(const [caption,command] of job.state==='paused'?[['继续补全','resume']]:['queued','working'].includes(job.state)?[['暂停补全','pause']]:['error','not_found'].includes(job.state)?[['重试补全','retry']]:[])
          toolAction(card,caption,'job/'+job.id,'/metadata/jobs/'+job.id+'/'+command,null,loadImports,r=>r.id===job.id);jobCards.append(card);}
      if(!jobs.length)empty(jobCards,'没有联网补全任务。');
      $('batches').replaceChildren(cards);$('jobs').replaceChildren(jobCards);$('imports-summary').textContent=batches.length+' 个批次 · '+batches.filter(b=>b.state!=='complete').length+' 个未结束 · '+jobs.filter(j=>j.state==='error'||j.state==='not_found').length+' 个任务待核对';
    }catch(e){if(current()&&tab==='imports')notice('进度刷新失败；保留最近成功显示。'+VnTools.failure(e),true);}finally{importsInFlight--;}
  }
  async function loadCandidates(){
    const current=toolsRead.next();try{
      const candidates=(await api('/metadata/candidates')).filter(c=>c.state==='pending'),cards=document.createDocumentFragment();
      for(const candidate of candidates){
        if(!current()||tab!=='review')return;
        const card=node('article',undefined,'manager-block');card.append(node('h3',candidate.incoming?.title||'标题查询候选'),node('p',candidate.message));
        if(candidate.incoming){VnTools.details(card,'导入资料（供比较）',candidate.incoming);let comparable=false;
          try{const old=await api((candidate.vndb_id.startsWith('p')?'/producers/':'/vns/')+candidate.vndb_id);VnTools.details(card,'Web现有资料（保留）',old.items[0]);comparable=true;}catch(e){card.append(node('p','现有资料读取失败，请刷新后比较。'+VnTools.failure(e),'notice error'));}
          if(comparable)toolAction(card,'确认采用导入资料','candidate/'+candidate.id,'/metadata/candidates/'+candidate.id+'/accept',{expected_revision:candidate.expected_revision},loadCandidates,r=>r.id===candidate.id);
        }
        for(const match of candidate.matches){const row=node('div',undefined,'tool-row');row.append(node('strong',match.title),node('p',match.vndb_id+' · '+match.developers.map(p=>p.original||p.name).join(' / '),'small'));
          toolAction(row,candidate.item_id?'确认关联此作品':'确认已核对','candidate/'+candidate.id,'/metadata/candidates/'+candidate.id+'/accept',{expected_revision:candidate.expected_revision,novel_id:match.vndb_id},loadCandidates,r=>r.id===candidate.id);card.append(row);}
        toolAction(card,'保留现有资料／拒绝候选','candidate/'+candidate.id,'/metadata/candidates/'+candidate.id+'/reject',{expected_revision:candidate.expected_revision},loadCandidates,r=>r.id===candidate.id);VnTools.details(card,'候选ID与原始证明',candidate);cards.append(card);
      }
      if(!current()||tab!=='review')return;if(!candidates.length)empty(cards,'没有待确认候选。');$('candidates').replaceChildren(cards);$('candidates-summary').textContent=candidates.length+' 个候选待明确选择；未经确认不会覆盖现有资料。';
    }catch(e){if(current()&&tab==='review')notice('候选读取失败；保留最近成功显示。'+VnTools.failure(e),true);}
  }
  async function loadRemoved(){
    const current=toolsRead.next();try{
      const [items,plays]=await Promise.all([api('/items?deleted=true&page_size=100'),api('/plays?deleted=true')]),cards=document.createDocumentFragment();
      if(!current()||tab!=='trash')return;
      for(const view of items.items){const item=view.item,card=node('article',undefined,'manager-block');card.append(node('h3','书架 · '+item.details.title),node('p','恢复保留原条目身份和历史；不涉及游戏文件。','small'));VnTools.details(card,'原条目与修订',item);toolAction(card,'恢复书架条目','item/'+item.id,'/items/'+item.id+'/restore',{expected_revision:item.revision},loadRemoved,r=>r.id===item.id);cards.append(card);}
      for(const play of plays){const card=node('article',undefined,'manager-block');card.append(node('h3','游玩 · '+play.details.title),node('p',VnTools.label(play.details.status)+' · '+play.updated_at,'small'));VnTools.details(card,'原经历与修订',play);toolAction(card,'恢复游玩记录','play/'+play.id,'/plays/'+play.id+'/restore',{expected_revision:play.revision},loadRemoved,r=>r.id===play.id);cards.append(card);}
      if(!items.total&&!plays.length)empty(cards,'没有已移除记录。');$('removed').replaceChildren(cards);$('removed-summary').textContent='书架 '+items.total+' 项（当前显示 '+items.items.length+'） · 游玩记录 '+plays.length+' 项；仅恢复明确选中的原记录。';
    }catch(e){if(current()&&tab==='trash')notice('移除记录读取失败；保留最近成功显示。'+VnTools.failure(e),true);}
  }

 const datasetRead=VnTools.latest();
 async function loadDatasets(){
  const current=datasetRead.next();
  try{
   const jobs=await api('/datasets');if(!current())return;
   const content=document.createDocumentFragment();
   const labels={queued:'待处理',downloading:'下载中',preparing:'预检中',importing:'合并中',paused:'已暂停',failed:'需核对后恢复',cancelled:'已取消',completed:'已完成'};
   for(const job of jobs){
    const card=node('article',undefined,'manager-block');
    card.append(node('h3',job.source+' · '+(labels[job.state]||job.state)),node('p',`已处理 ${job.processed} / ${job.total} · 写入 ${job.written} · 保留已有 ${job.kept}`,'small'));
    if(job.sha256)card.append(node('p','包SHA256：'+job.sha256,'small'));
    if(job.snapshot_at)card.append(node('p','源快照：'+job.snapshot_at,'small'));
    if(job.error)card.append(node('p',job.error,'notice'));
    if(job.earliest_resume_at)card.append(node('p','可恢复时间：'+job.earliest_resume_at,'small'));
    const actions=['queued','downloading','preparing','importing'].includes(job.state)?['pause','cancel']:['paused','failed'].includes(job.state)?['resume','cancel']:[];
    for(const action of actions)toolAction(card,{pause:'暂停',resume:'继续／重试',cancel:'取消任务（保留已写资料）'}[action],
      'dataset/'+job.id,'/datasets/'+job.id+'/'+action,{expected_revision:job.revision},loadDatasets,r=>r.id===job.id&&r.revision>job.revision);
    content.append(card);
   }
   if(!jobs.length)empty(content,'没有官方导入任务。');$('datasets').replaceChildren(content);
   $('datasets-summary').textContent='最近 '+jobs.length+' 个任务；完成仅表示所选公共资料处理通过，不含全量图片。';
  }catch(error){if(current())$('datasets-summary').textContent='进度读取失败，保留已显示内容。'+VnTools.failure(error);}
 }
 $('datasets-refresh').onclick=()=>explicitToolReload(loadDatasets);
 $('dataset-create').onclick=()=>{
  const date=$('dataset-date').value;if(!/^\d{4}-\d{2}-\d{2}$/.test(date)){notice('请选择官方导出日期。',true);return;}
  const card=node('article',undefined,'manager-block');card.append(node('p','将下载 '+date+' 的官方导出并在现有服务后台验证。'));
  toolAction(card,'确认建立任务','dataset/create','/datasets',{snapshot_date:date},loadDatasets,r=>typeof r.id==='string'&&r.source==='vndb-db-'+date+'.tar.zst');$('dataset-attempts').append(card);
 };
 document.querySelectorAll('[data-tab]').forEach(b=>b.onclick=()=>show(b.dataset.tab).catch(e=>notice(VnView.failure(e),true)));
 function closeItem(){if(!itemAttempt&&JSON.stringify(itemDraft())!==itemInitial&&!confirm('放弃尚未保存的书架字段修改？'))return;$('item-dialog').close();}
 $('item-dialog').addEventListener('cancel',e=>{e.preventDefault();closeItem();});
 window.addEventListener('beforeunload',e=>{if(itemAttempt){e.preventDefault();e.returnValue='';}});
 document.querySelectorAll('[data-close]').forEach(b=>b.onclick=()=>{if(b.dataset.close==='item-dialog')closeItem();else $(b.dataset.close).close();});$('new-item').onclick=()=>editItem();
 $('item-search').onsubmit=e=>{e.preventDefault();searchHistory.record($('item-query').value);change({query:$('item-query').value.trim(),unmatched:$('item-unmatched').checked});};
 $('item-unmatched').onchange=()=>change({unmatched:$('item-unmatched').checked});
 $('clear-filters').onclick=()=>change({query:'',unmatched:false,progress:'',presence:'',statusFilter:'',favorite:'',label:''});
 $('item-status').onchange=()=>change({statusFilter:$('item-status').value});
 $('item-quick').onchange=()=>{const patch=VnView.shelfQuickViews[$('item-quick').value];if(patch)change(patch);};
 $('item-progress').onchange=()=>change({progress:$('item-progress').value});
 $('item-presence').onchange=()=>change({presence:$('item-presence').value});
 if($('favorite-filter'))$('favorite-filter').onchange=()=>change({favorite:$('favorite-filter').value});
 if($('personal-label-filter'))$('personal-label-filter').onchange=()=>change({label:$('personal-label-filter').value});
 for(const name of ['layout','density','fit'])$('collection-'+name).onchange=()=>{display[name]=$('collection-'+name).value;remember();VnView.appearance($('items'),display);};
 for(const [id,delta] of [['items-prev',-1],['items-next',1]])$(id).onclick=()=>{remember();page=Math.max(1,page+delta);display.scroll=0;persist(true);loadBooks(true);};
 $('key-open').onclick=()=>{$('manager-key').value=key;$('key-dialog').showModal();};$('key-form').onsubmit=e=>{e.preventDefault();key=$('manager-key').value.trim();VnView.storage('sessionStorage').set('library-key',key);$('key-dialog').close();};
 $('refresh-imports').onclick=()=>explicitToolReload(loadImports).catch(e=>notice(e.message,true));$('refresh-candidates').onclick=()=>explicitToolReload(loadCandidates).catch(e=>notice(e.message,true));$('refresh-trash').onclick=()=>explicitToolReload(loadRemoved).catch(e=>notice(e.message,true));
 window.addEventListener('popstate',()=>{display=stored.read();page=display.page;controls();show(['imports','review','trash'].includes(new URLSearchParams(location.search).get('tab'))?new URLSearchParams(location.search).get('tab'):'shelf',{push:false,rememberOld:false}).catch(e=>notice(VnView.failure(e),true));});
 let scrollTimer;window.addEventListener('scroll',()=>{clearTimeout(scrollTimer);scrollTimer=setTimeout(remember,180);},{passive:true});window.addEventListener('pagehide',remember);
 controls();api('/info').then(info=>{limits=VnImport.limits(info);$('import-limits').textContent=VnImport.describe(limits);}).catch(e=>notice(e.message,true));
 show(['imports','review','trash'].includes(new URLSearchParams(location.search).get('tab'))?new URLSearchParams(location.search).get('tab'):'shelf',{push:false,rememberOld:false}).catch(e=>notice(VnView.failure(e),true));
 setInterval(()=>{if(tab==='imports'&&document.visibilityState==='visible'&&!importsInFlight)loadImports().catch(()=>{});},5000);
})();
