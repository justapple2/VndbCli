'use strict';
(() => {
  const $=id=>document.getElementById(id),make=VnTools.node;
  try{$('key').value=sessionStorage.getItem('library-key')||'';}catch{}
  const api=(path,options)=>VnTools.request('/api/v1'+path,options,$('key').value),writes=VnTools.writes(api),reads=VnTools.latest(),times=VnTools.latest();
  let summary=null;
  function notice(text){$('message').hidden=!text;$('message').textContent=text;}
  async function perform(action){try{await action();}catch(e){notice(VnTools.failure(e));}}
  function action(parent,label,slot,path,body,refresh,validate){return VnTools.action(parent,{label,slot,path,body,refresh,validate},writes);}
  function metric(parent,label,value){const d=make('div',undefined,'tool-metric');d.append(make('dt',label),make('dd',value));parent.append(d);}
  async function time(){
    const current=times.next(),id=$('play').value;summary=null;$('adjust-submit').disabled=true;
    if(!id){$('time').replaceChildren(make('p','暂无游玩经历。'));return;}
    try{
      const result=await api('/plays/'+encodeURIComponent(id)+'/time');if(!current()||$('play').value!==id)return;
      if(result.play_id!==id)throw Object.assign(Error('时间记录与所选经历不符。'),{code:'protocol_invalid'});
      summary=result;const metrics=make('dl',undefined,'tool-metrics');
      metric(metrics,'历史／手工基数',result.base_minutes===null?'未知':result.base_minutes+' 分钟');metric(metrics,'已确认会话',result.session_seconds+' 秒');metric(metrics,'人工调整',result.adjustment_seconds+' 秒');
      metric(metrics,'总计',result.total_seconds===null?'历史未知':(result.total_seconds/3600).toFixed(2)+' 小时');metric(metrics,'待核对会话',String(result.pending_sessions));
      $('time').replaceChildren(metrics);if(result.total_seconds===null)$('time').append(make('p','历史时间保持未知；新增会话和调整单独保留。','small'));
      VnTools.details($('time'),'时间原始证明与修订',result);$('adjust-submit').disabled=!!writes.peek('adjustment');
    }catch(e){if(current())notice('时间读取失败；上次显示保留，确认目标前不能调整。'+VnTools.failure(e));}
  }
  async function load(){
    const current=reads.next();
    try{
      const [plays,sessions,conflicts]=await Promise.all([api('/plays'),api('/play-sessions'),api('/play-edit-conflicts')]);if(!current())return;
      const selected=$('play').value,titles=new Map(plays.map(p=>[p.id,p.details.title])),sessionCards=document.createDocumentFragment(),conflictCards=document.createDocumentFragment();
      for(const s of sessions){const c=make('article',undefined,'manager-block');c.append(make('h3',titles.get(s.sample.play_id)||'经历待核对'),make('p',VnTools.label(s.sample.state)+' · '+s.sample.cumulative_seconds+' 秒 · '+VnTools.label(s.review_state),'tool-state'),make('p',s.sample.started_at,'small'));VnTools.details(c,'会话身份、设备与原始记录',s);
        if(!['active','paused'].includes(s.sample.state)&&s.review_state==='pending')for(const [label,include]of[['明确计入此次会话',true],['排除此会话',false]])action(c,label,'session/'+s.sample.id,'/play-sessions/'+encodeURIComponent(s.sample.id)+'/decision',{expected_revision:s.revision,include},load,r=>r.sample?.id===s.sample.id);
        sessionCards.append(c);
      }
      if(!sessions.length)sessionCards.append(make('p','暂无客户端会话；不影响基本启动。','manager-empty'));
      const pending=conflicts.filter(c=>c.state==='pending');
      for(const c of pending){const row=make('article',undefined,'manager-block');row.append(make('h3',c.current.details.title),make('p','个人编辑冲突：双方原稿保留，采用客户端字段也不会更改Web关联和时间基数。','small'));
        for(const [title,value]of[['Web当前个人字段',c.current.details],['客户端个人字段',c.incoming.details]]){const part=make('section',undefined,'tool-row');part.append(make('h4',title),make('p','状态：'+VnTools.label(value.status)),make('p',value.review||'尚无感想'));VnTools.details(part,'完整个人字段',value);row.append(part);}
        for(const [label,useIncoming]of[['保留Web版本',false],['采用客户端个人字段',true]])action(row,label,'conflict/'+c.id,'/play-edit-conflicts/'+encodeURIComponent(c.id)+'/decision',{expected_revision:c.revision,play_revision:c.current.revision,use_incoming:useIncoming},load,r=>r.id===c.id);
        VnTools.details(row,'双方原记录、身份与修订',c);conflictCards.append(row);
      }
      if(!pending.length)conflictCards.append(make('p','没有待处理的个人编辑冲突。','manager-empty'));
      $('play').replaceChildren();for(const p of plays){const option=make('option',p.details.title+' · '+VnTools.label(p.details.status)+' · '+(p.details.started_on||'日期未知')+' · '+p.id);option.value=p.id;$('play').append(option);}
      if(plays.some(p=>p.id===selected))$('play').value=selected;
      $('sessions').replaceChildren(sessionCards);$('conflicts').replaceChildren(conflictCards);$('session-summary').textContent=sessions.length+' 个会话 · '+sessions.filter(s=>s.review_state==='pending').length+' 个待核对 · '+pending.length+' 个个人编辑冲突';await time();
    }catch(e){if(current())notice('刷新失败；保留最近成功显示。'+VnTools.failure(e));}
  }
  function receiptCard(name,receipt){
    const card=make('article',undefined,'manager-block');card.append(make('h3',name),make('p',receipt.complete?'Web已完成本次消费；仍请检查逐项冲突结果。':'Web已接收；依赖或冲突仍待核对。','tool-summary'));
    const rows=make('details');rows.open=!receipt.complete||receipt.results.some(r=>['conflict','error','invalid','dependency_pending'].includes(r.status));rows.append(make('summary','逐项结果（'+receipt.results.length+'）'));
    for(const r of receipt.results){const part=make('div',undefined,'tool-row');part.append(make('strong',VnTools.label(r.kind)+' · '+r.key),make('p',VnTools.label(r.status),'tool-state'));if(r.message)part.append(make('p',r.message));rows.append(part);}card.append(rows);VnTools.details(card,'原回执及服务实例',receipt);return card;
  }
  $('import').onclick=()=>perform(async()=>{
    $('import').disabled=true;try{
      const files=Array.from($('files').files);if(!files.length)throw Error('先选择活动JSON文件。');const limits=VnImport.limits(await api('/info'));$('activity-limits').textContent=VnImport.describe(limits);
      for(const file of files){const block=make('article',undefined,'manager-block');block.append(make('h3',file.name));$('receipts').append(block);
        try{const payload=await VnImport.read(file,'activity',limits);const submit=action(block,'提交此活动批次','activity/'+payload.batch_id,'/activity/batches',payload,async receipt=>{block.append(receiptCard(file.name,receipt));await load();},r=>r.batch_id===payload.batch_id&&Array.isArray(r.results)&&typeof r.complete==='boolean');await submit.onclick();}catch(e){block.append(make('p',VnTools.failure(e),'notice error'));}
      }
    }finally{$('import').disabled=false;}
  });
  $('adjust').onsubmit=e=>{e.preventDefault();perform(async()=>{
    if(!summary||summary.play_id!==$('play').value)throw Error('先读取所选游玩经历的最新时间。');const seconds=Number($('seconds').value),reason=$('reason').value;if(!Number.isSafeInteger(seconds)||!reason.trim())throw Error('请输入整数秒和调整原因。');
    const basis=summary,body={id:crypto.randomUUID(),play_id:basis.play_id,seconds,reason,expected_revision:basis.revision},group=$('adjust-result');
    if(writes.peek('adjustment'))throw Error('原调整仍待处理；请在原操作中重试或明确重新读取。');group.replaceChildren();const submit=action(group,'确认本次人工调整','adjustment','/time-adjustments',body,load,r=>r.id===body.id&&r.play_id===body.play_id&&r.seconds===body.seconds);await submit.onclick();
  });};
  $('refresh').onclick=()=>perform(async()=>{writes.reload();$('tool-history').replaceChildren();if(writes.archived.length)VnTools.details($('tool-history'),'重新读取前保留的原操作',writes.archived.map(e=>({id:e.id,path:e.path,body:JSON.parse(e.json)})));await load();});
  $('play').onchange=()=>perform(time);window.addEventListener('beforeunload',e=>{if(writes.unsettled){e.preventDefault();e.returnValue='';}});
  perform(async()=>{$('activity-limits').textContent=VnImport.describe(VnImport.limits(await api('/info')));await load();});
})();
