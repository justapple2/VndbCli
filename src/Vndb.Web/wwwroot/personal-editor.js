/* Personal editing presentation only. Writes use the existing revisioned v1 API. */
(function(root, factory) {
 'use strict'; const value=factory(typeof module==='object'?require('./field-comparison.js'):root.VnDifferences); if(typeof module==='object')module.exports=value;else root.VnPersonal=value;
})(globalThis, function(comparison) {
 'use strict';
 const labels={not_started:'未游玩',playing:'游玩中',played:'已游玩',completed:'本次完成',on_hold:'搁置',dropped:'放弃'};
 const clone=value=>structuredClone(value), same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
 const operation=()=>globalThis.crypto?.randomUUID?.()||Date.now().toString(36)+'-'+Math.random().toString(36).slice(2);
 const editorKey=(record,item)=>record?'play:'+record.id:'item:'+(item?.id??'unselected')+'|new';
 function session(record,item=null,newId=operation) {
  let original=record?clone(record):null, pending=null, state='editing', archived=[],observed=null,observedKnown=false,observedAt=null;
  const itemId=item?.id??record?.item_id??null;
  if(!itemId)throw Error('请先在我的书架选择具体条目或核对历史记录的关联，再编辑游玩；不会按标题或VNDB ID猜测版本。');
  function requireTarget(value) {
   if(!value?.id||value.deleted_at||value.item_id!==itemId)throw Error('条目归属已改变；当前原稿保留，请核对书架版本。');
  }
  if(record)requireTarget(record);
  return {
   get record(){return original&&clone(original);}, get pending(){return pending&&clone(pending);},
   get state(){return state;},get archived(){return clone(archived);},get itemId(){return itemId;},
   get observedAt(){return observedAt;},
   observe(value){if(original&&value&&value.id!==original.id)throw Error('Web对照记录身份不符。');observed=value?clone(value):null;observedKnown=!!original;observedAt=new Date().toISOString();},
   review(incoming){return comparison.play(original?.details,observed?.details,pending?.body.details||incoming,{currentKnown:observedKnown});},
   async submit(details,send) {
    if(state==='applied'||state==='submitting'||state==='conflict')throw Error('请先核对当前操作状态。');
    if(!pending)pending={path:'/plays'+(original?'/'+encodeURIComponent(original.id):''),method:original?'PATCH':'POST',
     body:{details:clone(details),item_id:itemId,expected_revision:original?.revision??null},operationId:newId()};
    state='submitting';
    try {
     const saved=await send(clone(pending)); requireTarget(saved);
     if(original&&saved.id!==original.id||saved.revision!==(original?.revision??0)+1||!same(saved.details,pending.body.details))
      throw Error('保存结果与本次原稿不符，尚未确认；请按原操作重试。');
     state='applied';return saved;
    }catch(error){state=error.status===409?'conflict':error.status>=400&&error.status<500&&error.status!==408&&error.status!==429?'rejected':'uncertain';throw error;}
   },
   reload(value) {
    if(!['conflict','rejected'].includes(state))throw Error('结果尚未确认，只能重试原操作。');
    if(original){requireTarget(value);if(value.id!==original.id||value.details.novel_id!==original.details.novel_id)throw Error('原记录关联已变化，原稿保留。');}
    if(pending)archived.push({...clone(pending),basis:clone(original)});original=value?clone(value):original;pending=null;state='editing';observed=null;observedKnown=false;observedAt=null;
   }
  };
 }
 function details(values,basis={}) {
  const nullable=(name)=>values[name]===(typeof basis[name]==='string'?basis[name].replace(/\r\n?/g,'\n'):basis[name]??'')?basis[name]??null:values[name]||null;
  const hours=Number(values.hours||0),minutes=Number(values.minutes||0);
  if(values.known&&(!Number.isInteger(hours)||hours<0||!Number.isInteger(minutes)||minutes<0||minutes>59||hours*60+minutes>2147483647))throw Error('小时为非负整数，分钟为0–59，总时长不能溢出。');
  const score=values.score===''?null:Number(values.score);
  if(score!==null&&(!Number.isFinite(score)||score<0||score>10))throw Error('个人评分须为0–10，未知时留空。');
  if(!labels[values.status]||!values.title.trim())throw Error('请填写标题并选择有效状态。');
  const duration=values.known?hours*60+minutes:null;
  if(values.status==='not_started'&&(duration>0||values.started_on||values.finished_on))throw Error('未游玩不能填写实际游玩日期或正时长。');
  if(values.started_on&&values.finished_on&&values.started_on>values.finished_on)throw Error('结束日期不能早于开始日期。');
  return {title:values.title.trim(),novel_id:nullable('novel_id'),status:values.status,started_on:nullable('started_on'),finished_on:nullable('finished_on'),duration_minutes:duration,
   score,review:nullable('review'),notes:nullable('notes'),extra_notes:nullable('extra_notes'),language:nullable('language')};
 }
 function time(record) {
  const d=record.details,t=record.time;
  return '手工基数：'+(d.duration_minutes==null?'未知':d.duration_minutes+' 分钟')+(t?' · 会话 '+t.session_seconds+' 秒 · 调整 '+t.adjustment_seconds+' 秒\n合计：'+(t.total_seconds==null?'未知':(t.total_seconds/3600).toLocaleString('zh-CN',{maximumFractionDigits:2})+' 小时')+' · 待核对 '+t.pending_sessions:'');
 }
 async function request(path,{method='GET',body,operationId}={}) {
  const key=globalThis.VnView.storage('sessionStorage').get('library-key')||'';
  const response=await fetch('/api/v1'+path,{method,headers:{'X-Library-Request':'1',...(key?{'X-Library-Key':key}:{}),...(body===undefined?{}:{'Content-Type':'application/json'}),...(operationId?{'Idempotency-Key':operationId}:{})},
   body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(300000)});
  const value=await response.json().catch(()=>null);
  if(!response.ok)throw Object.assign(Error(value?.title||'请求失败 '+response.status),{status:response.status});
  if(!value)throw Error('返回数据无效，结果尚未确认。');return value;
 }
 const element=(tag,text,className)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(className)e.className=className;return e;};
 function action(parent,text,run,kind='secondary') {
  const b=element('button',text,kind);b.type='button';let running=false;b.onclick=async()=>{if(running)return;running=true;b.setAttribute('aria-busy','true');try{await run();}catch(e){parent.append(element('p',e.message,'notice error'));}finally{running=false;b.removeAttribute('aria-busy');}};parent.append(b);return b;
 }
 function create({changed=async()=>{}}={}) {
  const retained=new Map();let current=null,basis={},initial='',returnFocus=null,readGeneration=0,busy=false,comparisonRead=0;
  const dialog=element('dialog',undefined,'personal-editor'),heading=element('h2','游玩记录');heading.id='personal-editor-title';dialog.setAttribute('aria-labelledby',heading.id);
  const context=element('p',undefined,'personal-context'),form=element('form'),fields=element('fieldset'),grid=element('div',undefined,'personal-grid'),inputs={};
  function input(name,label,type='text',maxLength) {
   const wrap=element('label',label),control=element(type==='textarea'?'textarea':type==='select'?'select':'input');
   if(name==='known'||name==='novel_id')wrap.className='personal-wide';if(name==='known')wrap.classList.add('personal-known');
   if(control.tagName==='INPUT')control.type=type;if(maxLength)control.maxLength=maxLength;
   control.name=name;inputs[name]=control;wrap.append(control);(type==='textarea'?fields:grid).append(wrap);return control;
  }
  fields.append(grid);
  input('title','记录标题','text',500).required=true;
  const status=input('status','游玩状态','select');for(const [code,label] of Object.entries(labels))status.append(new Option(label,code));
  input('started_on','开始日期（可空）','date');input('finished_on','结束日期（可空）','date');
  input('known','手工时长已知','checkbox');
  for(const [name,label,max] of [['hours','手工小时',35791394],['minutes','手工分钟',59]]){const c=input(name,label,'number');c.min=0;c.max=max;c.step=1;}
  const score=input('score','个人评分（未知留空）','number');score.min=0;score.max=10;score.step='any';
  input('language','游玩语言','text',40);input('novel_id','VNDB ID（未关联可空）','text',40).pattern='v[1-9][0-9]*';
  for(const [name,label,max] of [['review','个人评价',20000],['notes','备注一',10000],['extra_notes','备注二',10000]]){const c=input(name,label,'textarea',max);c.rows=name==='review'?4:2;}
  const summary=element('p',undefined,'personal-context'),feedback=element('p',undefined,'notice'),history=element('details',undefined,'personal-originals'),historyBody=element('div');history.append(element('summary','保留的冲突／拒绝原稿（当前页面内）'),historyBody);
  feedback.setAttribute('role','status');
  const reviewSection=element('details',undefined,'personal-comparison'),reviewActions=element('div',undefined,'personal-actions-inline'),reviewBody=element('div'),reviewMessage=element('p',undefined,'personal-context');
  reviewSection.append(element('summary','原值／Web值／本机稿对照'),reviewActions,reviewMessage,reviewBody);
  const preview=action(reviewActions,'预览字段变化',async()=>showComparison()),readCurrent=action(reviewActions,'只读当前Web对照',async()=>{
   const target=current,record=target?.record,token=++comparisonRead,key=target?.pending?.operationId||record?.revision;
   if(!record){reviewMessage.textContent='新建请求不能凭相似记录判断结果；待确认操作只按原ID重试。';return;}
   let latest;try{latest=await request('/plays/'+encodeURIComponent(record.id));}catch(error){if(error.status!==404)throw error;latest=null;}
   if(current!==target||!dialog.open||token!==comparisonRead||(target.pending?.operationId||target.record?.revision)!==key)return;
   target.observe(latest);showComparison();reviewMessage.textContent='只读观测 '+new Date().toLocaleString()+'；操作ID、原稿和同步状态未改变。'+(latest&&(latest.deleted_at||latest.item_id!==target.itemId||latest.details.novel_id!==target.record.details.novel_id)?'记录移除或关联已变化，请回书架核对。':'');
  });
  const bar=element('div',undefined,'personal-actions'),save=element('button','保存本次记录','primary'),retry=element('button','重试原操作','primary'),reload=element('button','保留原稿并重新读取','secondary'),cancel=element('button','取消／返回','plain');
  save.type='submit';for(const b of [retry,reload,cancel])b.type='button';bar.append(save,retry,reload,cancel);form.append(fields,summary,feedback,reviewSection,history,bar);dialog.append(heading,context,form);document.body.append(dialog);
  const values=()=>Object.fromEntries(Object.entries(inputs).map(([k,c])=>[k,c.type==='checkbox'?c.checked:c.value]));
  const dirty=()=>JSON.stringify(values())!==initial;
  function showComparison(){if(!current||!comparison)return;const incoming=current.pending?.body.details||details(values(),basis);comparison.render(reviewBody,current.review(incoming),comparison.outcome(current.state));reviewSection.open=true;}
  function update() {
   fields.disabled=busy||current?.pending!==null;
   save.hidden=current?.pending!==null;save.disabled=busy;
   retry.hidden=!current?.pending||['conflict','rejected','applied'].includes(current.state);retry.disabled=busy;
   reload.hidden=!['conflict','rejected'].includes(current?.state);reload.disabled=busy;cancel.disabled=busy;
   inputs.hours.disabled=inputs.minutes.disabled=!inputs.known.checked;
   history.hidden=!current?.archived.length;historyBody.replaceChildren();
   for(const entry of current?.archived||[]){const block=element('section');historyBody.append(block);if(comparison)comparison.render(block,comparison.play(entry.basis?.details,current.record?.details,entry.body.details,{originalKnown:'basis'in entry,currentKnown:true}), '已保留原操作 '+entry.operationId);else block.append(element('pre',JSON.stringify(entry.body,null,2)));}
   preview.disabled=readCurrent.disabled=busy;
   if(current?.pending&&comparison)showComparison();
  }
  function populate(d) {
   comparisonRead++;reviewBody.replaceChildren();reviewSection.open=false;reviewMessage.textContent=current.observedAt?'Web观测 '+new Date(current.observedAt).toLocaleString():'Web当前值尚未读取；预览不发送请求。';
   basis=clone(d);for(const [name,c] of Object.entries(inputs)){if(name==='known')c.checked=d.duration_minutes!=null;else c.value=name==='hours'?d.duration_minutes==null?'':Math.floor(d.duration_minutes/60):name==='minutes'?d.duration_minutes==null?'':d.duration_minutes%60:d[name]??'';}
   inputs.novel_id.readOnly=true;initial=JSON.stringify(values());
   context.textContent='条目 '+current.itemId+'\n'+(current.record?'记录 '+current.record.id+' · 修订 '+current.record.revision:'新的独立记录，原有记录保留');
   summary.textContent=(current.record?time(current.record)+'\n':'')+'只修改手工基数；自动会话、调整与原表来源保留。未知与零分钟不同。';update();
  }
  function close() {
   if(busy)return;
   if(!current.pending&&dirty()&&!confirm('放弃当前未保存修改并返回？'))return;
   if(!current.pending&&!current.archived.length)retained.delete(current.cacheKey);
   dialog.close();current=null;
   if(returnFocus?.isConnected)returnFocus.focus();
  }
  form.onsubmit=async e=>{e.preventDefault();if(busy||!current)return;let d;
   try{d=details(values(),basis);}catch(error){feedback.textContent=error.message;return;}
   await submit(d);
  };
  async function submit(d) {
   busy=true;feedback.textContent='正在保存…';const target=current;const task=target.submit(d,op=>request(op.path,op));update();
   try{const saved=await task;retained.delete(target.cacheKey);dialog.close();current=null;
    try{await changed();}catch{ /* Write is confirmed; a failed refresh must not resubmit it. */ }
    if(returnFocus?.isConnected)returnFocus.focus();else document.querySelector('[data-play-id="'+CSS.escape(saved.id)+'"] button')?.focus();
   }catch(error){feedback.textContent=error.message+'；当前页面原稿保留。'+(target.state==='uncertain'?'结果尚未确认，只能按原操作重试。':target.state==='conflict'?'请核对Web版本后重新编辑。':'核对密钥或内容后可重试／返回修改。');}
   finally{busy=false;update();if(current===target)(target.state==='uncertain'?retry:reload).focus();}
  }
  retry.onclick=()=>submit(current.pending.body.details);
  reload.onclick=async()=>{if(busy||!current)return;busy=true;update();try{const latest=current.record?await request('/plays/'+encodeURIComponent(current.record.id)):null;current.reload(latest);populate(latest?.details||basis);feedback.textContent='原稿保留在下方；请核对后重新编辑。';}catch(e){feedback.textContent=e.message;}finally{busy=false;update();(current.state==='editing'?inputs.status:reload).focus();}};
  inputs.known.onchange=update;cancel.onclick=close;dialog.addEventListener('cancel',e=>{e.preventDefault();close();});
  globalThis.addEventListener('beforeunload',e=>{if(retained.size||current&&dirty()){e.preventDefault();e.returnValue='';}});
  return {async open(record=null,item=null) {
   const trigger=document.activeElement,generation=++readGeneration;const cacheKey=editorKey(record,item);
   const existing=retained.get(cacheKey);
   if(existing&&item&&existing.itemId!==item.id)throw Error('此记录已有其他条目归属的待核对原稿，不能切换其目标。');
   if(!existing&&record)record=await request('/plays/'+encodeURIComponent(record.id));
   if(generation!==readGeneration)return;
   if(dialog.open)return;returnFocus=trigger?.isConnected?trigger:document.activeElement;
   current=existing||Object.assign(session(record,item),{cacheKey});
   const d=current.pending?.body.details||current.record?.details||{title:item?.details.title||'',novel_id:item?.details.novel_id||null,status:'playing',duration_minutes:null,score:null,review:null,notes:null,extra_notes:null,language:null,started_on:null,finished_on:null};
   // Retain in memory only once a write is attempted; cancelled input never writes to storage.
   const submitOnce=current.submit;if(!existing)current.submit=async(...args)=>{retained.set(cacheKey,current);return submitOnce(...args);};
   heading.textContent=current.record?'编辑这次游玩':'新建独立游玩／重玩';
   context.textContent=(current.itemId?'条目 '+current.itemId:'历史记录未关联个人条目；不会按标题合并')+'\n'+(current.record?'记录 '+current.record.id+' · 修订 '+current.record.revision:'新的独立记录，原有记录保留');
   summary.textContent=(current.record?time(current.record)+'\n':'')+'只修改手工基数；自动会话、调整与原表来源保留。未知与零分钟不同。';
   feedback.textContent=existing?(current.state==='editing'?'已恢复此页面内的核对记录，原稿保留在下方。':['conflict','rejected'].includes(current.state)?'已恢复待核对原稿，请保留原稿并重新读取。':'已恢复当前页面内的待确认原稿，请按原操作重试。'):'';populate(d);dialog.showModal();
   (current.pending?cancel:inputs.status).focus();
  }};
 }
 async function itemsForNovel(novelId) {
  const found=[];for(let page=1;page<=100;page++){
   const result=await request('/items?'+new URLSearchParams({page,page_size:100}));
   found.push(...result.items.filter(v=>v.item.details.novel_id===novelId));if(!result.more)return found;
  }throw Error('个人书架过大，请从我的书架选择具体条目。');
 }
 function playCard(record,edit) {
  const block=element('article',undefined,'personal-record');block.dataset.playId=record.id;block.append(element('h4',labels[record.details.status]||'状态待核对'),element('p',record.id+' · 修订 '+record.revision,'small'),element('p',time(record),'personal-context'));
  if(record.details.review)block.append(element('p',record.details.review,'personal-review'));
  const extra=element('details');extra.append(element('summary','日期、评分与备注'));
  for(const [label,value] of [['日期',(record.details.started_on||'开始未知')+' → '+(record.details.finished_on||'结束未知')],['个人评分',record.details.score==null?'未评分':record.details.score+' / 10'],['备注一',record.details.notes],['备注二',record.details.extra_notes],['语言',record.details.language]])if(value!=null)extra.append(element('p',label+'：'+value));block.append(extra);action(block,'编辑这次记录',()=>edit(record));return block;
 }
 return {session,details,time,editorKey,create,request,itemsForNovel,playCard,element,action};
});
