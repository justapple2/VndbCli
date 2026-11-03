/* Flat virtual collections/tags and favorites. File locations never enter this module. */
(function(root,factory){const api=factory(typeof module==='object'?require('./field-comparison.js'):root.VnDifferences);if(typeof module==='object')module.exports=api;else root.VnOrganization=api;})(globalThis,comparison=>{
 'use strict';
 const validId=id=>typeof id==='string'&&/^[A-Za-z0-9_-]{1,100}$/.test(id);
 function validate(value){
  if(!value||!Number.isSafeInteger(value.revision)||value.revision<0||!Array.isArray(value.labels)||value.labels.length>200||!Array.isArray(value.items)||value.items.length>20000)throw Error('个人分类格式无效，最近数据保留。');
  const ids=new Set(),members=new Set();
  for(const l of value.labels){if(!validId(l?.id)||ids.has(l.id)||!['collection','tag'].includes(l.kind)||typeof l.name!=='string'||!l.name.trim()||l.name.length>80)throw Error('个人分类身份无效。');ids.add(l.id);}
  for(const i of value.items){if(!validId(i?.item_id)||members.has(i.item_id)||typeof i.favorite!=='boolean'||!Array.isArray(i.label_ids)||i.label_ids.length>32||new Set(i.label_ids).size!==i.label_ids.length||i.label_ids.some(id=>!ids.has(id)))throw Error('个人分类成员无效。');members.add(i.item_id);}
  return value;
 }
 function confirms(result,body){
  validate(result);if(result.revision!==body.expected_revision+1)return false;
  if(body.label)return result.labels.some(l=>l.id===body.label.id&&l.name===body.label.name&&l.kind===body.label.kind);
  if(body.remove_label)return !result.labels.some(l=>l.id===body.remove_label)&&!result.items.some(i=>i.label_ids.includes(body.remove_label));
  return body.items.every(edit=>{const row=result.items.find(i=>i.item_id===edit.item_id);return (!row&&!edit.favorite&&!edit.label_ids.length)||row&&row.favorite===edit.favorite&&JSON.stringify(row.label_ids)===JSON.stringify(edit.label_ids);});
 }
 function create({api,changed,writer=VnTools.writes(api)}){
  let state={revision:0,labels:[],items:[]},loaded=false,read=0,manager=null,editor=null,status=null,editorStatus=null,busy=false,instance=null;
  let pendingBasis=null,observed=null,observedAt=null,reviewSequence=0;
  const node=(tag,text,cls)=>VnView.element(tag,cls,text),labelName=l=>(l.kind==='collection'?'集合 · ':'标签 · ')+l.name;
  function button(parent,text,run){const b=node('button',text,'secondary');b.type='button';b.onclick=async()=>{if(busy)return;busy=true;b.disabled=true;try{await run();}catch(e){message(e.message,true);}finally{busy=false;b.disabled=false;}};parent.append(b);return b;}
  function message(text,error=false){if(editorStatus){editorStatus.textContent=text;editorStatus.className=error?'notice error':'notice';}if(status){status.textContent=text;status.className=error?'notice error':'notice';}const pending=writer.peek('organization');if(pending){for(const parent of [status,editorStatus].filter(Boolean)){parent.append(node('p',comparison?.outcome(pending.state)||'原操作待核对'));if(comparison&&pendingBasis){const block=node('section');parent.append(block);comparison.render(block,review().rows,'队首原操作字段对照');}button(parent,'只读当前Web对照',()=>readReview());if(['uncertain','retryable'].includes(pending.state))button(parent,'按原操作重试',async()=>accept(await writer.retry('organization')));}if(status)VnTools.details(status,'原始操作证明（高级）',writer.proof('organization'));}}
  function accept(value){state=validate(value);loaded=true;pendingBasis=null;observed=null;observedAt=null;renderManager();renderEditor();changed?.(true);return state;}
  function review(){const pending=writer.proof('organization');return {state:pending?.state||'applied',observedAt,rows:pending&&pendingBasis&&comparison?comparison.organization(pendingBasis,observed,pending.body.change):[]};}
  async function readReview(){
   const pending=writer.peek('organization');if(!pending)return review();const token=++reviewSequence;
   const [info,current]=await Promise.all([api('/info',{method:'GET'}),api('/personal-organization',{method:'GET'})]);
   if(token!==reviewSequence||writer.peek('organization')?.id!==pending.id)return review();
   if(info.service_instance_id!==instance)throw Error('服务实例改变，原稿保留。');observed=validate(current);observedAt=new Date().toISOString();message('只读观测 '+new Date(observedAt).toLocaleString()+'；未确认原操作是否应用，状态与ID保持。');return review();
  }
  async function refresh(resolve=false){
   const pending=writer.peek('organization');if(pending){if(!resolve||!['conflict','rejected'].includes(pending.state)){message('分类原操作待处理；结果不明时只能按原ID重试。',true);return state;}writer.reload();}
   const ticket=++read;const [info,saved]=await Promise.all([api('/info',{method:'GET'}),api('/personal-organization',{method:'GET'})]);const result=validate(saved);if(ticket!==read)return state;if(!info.service_instance_id||!info.capabilities?.includes('personal_organization')||instance&&instance!==info.service_instance_id)throw Error('服务实例或分类能力已改变，原稿保留。');instance=info.service_instance_id;
   state=result;loaded=true;pendingBasis=null;observed=null;observedAt=null;renderManager();renderEditor();message('分类已读取。收藏与分类独立于游玩状态和本机已有。');changed?.(false);return state;
  }
  async function write(fields){
   if(!loaded)throw Error('请先读取个人分类。');
   const body={expected_revision:state.revision,...fields};
   if(!writer.peek('organization')){const ids=new Set((body.items||[]).map(i=>i.item_id));pendingBasis=structuredClone({revision:state.revision,labels:state.labels,items:state.items.filter(i=>ids.has(i.item_id))});observed=null;observedAt=null;}
   return accept(await writer.execute('organization','/personal-organization',{service_instance_id:instance,change:body},r=>confirms(r,body)));
  }
  function badge(itemId){const row=state.items.find(i=>i.item_id===itemId);return row?node('span',[row.favorite?'★ 收藏':'',...state.labels.filter(l=>row.label_ids.includes(l.id)).map(l=>l.name)].filter(Boolean).join(' · ')):null;}
  function filter(select,current){select.replaceChildren(new Option('全部集合／标签',''));for(const l of state.labels)select.append(new Option(labelName(l),l.id));if(current&&!state.labels.some(l=>l.id===current))select.append(new Option('已移除的分类',current));select.value=current||'';}
  function mountManager(parent){manager=parent;renderManager();}
  function renderManager(){if(!manager)return;manager.replaceChildren();status=node('div','','notice');status.setAttribute('role','status');manager.append(status);
   const row=node('div',undefined,'manager-two'),name=node('input'),kind=node('select'),select=node('select');name.maxLength=80;name.setAttribute('aria-label','个人分类名称');kind.setAttribute('aria-label','个人分类类型');select.setAttribute('aria-label','管理个人集合或标签');
   kind.append(new Option('虚拟集合','collection'),new Option('个人标签','tag'));select.append(new Option('选择已有分类',''));for(const l of state.labels)select.append(new Option(labelName(l),l.id));
   select.onchange=()=>{const l=state.labels.find(l=>l.id===select.value);if(l){name.value=l.name;kind.value=l.kind;}};row.append(select,name,kind);manager.append(row);
   button(manager,'读取分类',()=>refresh());button(manager,'保留冲突原稿并读取Web',()=>refresh(true));
   button(manager,'新建集合／标签',()=>write({label:{id:crypto.randomUUID(),name:name.value.trim(),kind:kind.value}}));
   button(manager,'改名',()=>{const l=state.labels.find(l=>l.id===select.value);if(!l)throw Error('请选择分类。');return write({label:{...l,name:name.value.trim()}});});
   button(manager,'移除分类（保留作品与文件）',()=>{if(!select.value)throw Error('请选择分类。');if(confirm('只移除此分类及成员关系，不删除作品或游戏，继续？'))return write({remove_label:select.value});});
   if(writer.archived.length)VnTools.details(manager,'重读前保留的分类原稿',writer.archived.map(e=>({id:e.id,body:JSON.parse(e.json),state:e.state})));
   message(writer.unsettled?'分类有原操作待处理。':'可创建虚拟集合与标签；不会改动物理目录。');
  }
  function mountEditor(parent,itemId){editor={parent,itemId};renderEditor();}
  function renderEditor(){if(!editor)return;const {parent,itemId}=editor;parent.replaceChildren();if(!itemId)return;
   editorStatus=node('div','','notice');editorStatus.setAttribute('role','status');parent.append(node('h3','个人收藏、集合与标签'),editorStatus);const old=state.items.find(i=>i.item_id===itemId),favorite=node('input');favorite.type='checkbox';favorite.checked=old?.favorite===true;
   const favoriteLabel=node('label','★ 个人收藏');favoriteLabel.prepend(favorite);parent.append(favoriteLabel);const checks=[];
   for(const l of state.labels){const check=node('input');check.type='checkbox';check.checked=old?.label_ids.includes(l.id)||false;const label=node('label',labelName(l));label.prepend(check);parent.append(label);checks.push([l.id,check]);}
   button(parent,'保存此作品的收藏与分类',()=>write({items:[{item_id:itemId,favorite:favorite.checked,label_ids:checks.filter(([,c])=>c.checked).map(([id])=>id)}]}));
  }
  return {refresh,write,review,readReview,badge,filter,mountManager,mountEditor,get state(){return state;},get unsettled(){return writer.unsettled;}};
 }
 return {validate,confirms,create};
});
