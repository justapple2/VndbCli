/* Read-only personal field projections. No requests, persistence, merges or operation-key changes. */
(function(root,factory){const value=factory();if(typeof module==='object')module.exports=value;else root.VnDifferences=value;})(globalThis,()=>{
 'use strict';
 const playLabels={title:'记录标题',novel_id:'作品关联',status:'游玩状态',started_on:'开始日期',finished_on:'结束日期',duration_minutes:'手工时长',score:'个人评分',review:'个人评价',notes:'备注一',extra_notes:'备注二',language:'游玩语言'};
 const itemLabels={title:'条目标题',novel_id:'作品关联',company_hint:'会社提示',title_override:'显示标题覆盖',company_override:'会社覆盖',notes:'备注一',extra_notes:'备注二'};
 const same=(a,b)=>a.known===b.known&&a.exists===b.exists&&a.value===b.value;
 function field(key,label,b,c,d,{originalKnown=true,currentKnown=false,originalExists=true,currentExists=true,incomingExists=true}={}){
  const value=(v,known,exists)=>({value:v===undefined?null:v,known,exists}),original=value(b,originalKnown,originalExists),current=value(c,currentKnown,currentExists),incoming=value(d,true,incomingExists);
  const draftChanged=originalKnown&&!same(original,incoming),remoteChanged=originalKnown&&currentKnown&&!same(original,current);
  return {key,label,original,current,incoming,draftChanged,remoteChanged,conflict:draftChanged&&remoteChanged&&!same(current,incoming)};
 }
 const visible=r=>!r.original.known||r.draftChanged||r.remoteChanged;
 function fields(before,current,incoming,labels,options={}){
  return Object.entries(labels).map(([key,label])=>field(key,label,before?.[key],current?.[key],incoming?.[key],{originalExists:before!=null,currentExists:current!=null,...options})).filter(visible);
 }
 const play=(before,current,incoming,options)=>fields(before,current,incoming,playLabels,options);
 const item=(before,current,incoming,options)=>fields(before,current,incoming,itemLabels,options);
 function organization(before,current,change){
  const rows=[],id=change.label?.id||change.remove_label;
  if(id){const b=before.labels.find(l=>l.id===id),c=current?.labels.find(l=>l.id===id),d=change.label;
   for(const [key,label]of [['name','名称'],['kind','类型']])rows.push(field('label:'+id+':'+key,'分类 '+id+' · '+label,b?.[key],c?.[key],d?.[key],{originalExists:!!b,currentKnown:!!current,currentExists:!!c,incomingExists:!!d}));}
  const names=(state,ids)=>[...(ids||[])].sort().map(id=>(state.labels.find(l=>l.id===id)?.name||'已移除分类')+' ['+id+']').join(' / ');
  const oldItems=new Map(before.items.map(i=>[i.item_id,i])),latest=new Map((current?.items||[]).map(i=>[i.item_id,i]));
  for(const edit of change.items||[]){const b=oldItems.get(edit.item_id),c=latest.get(edit.item_id),prefix='item:'+edit.item_id+':';
   rows.push(field(prefix+'favorite','条目 '+edit.item_id+' · 收藏',b?.favorite===true,c?.favorite===true,edit.favorite,{currentKnown:!!current}));
   rows.push(field(prefix+'labels','条目 '+edit.item_id+' · 分类',names(before,b?.label_ids),current?names(current,c?.label_ids):null,names(before,edit.label_ids),{currentKnown:!!current}));}
  return rows.filter(visible);
 }
 function outcome(state){return ({editing:'尚未保存',ready:'待同步',pending:'待同步',submitting:'正在提交',uncertain:'结果未确认：按原ID重试',conflict:'明确冲突：保留原稿后核对',rejected:'明确拒绝：核对内容或权限',applied:'已应用',retryable:'可按原ID重试'})[state]||'状态待核对';}
 function format(value,key){
  if(!value.known)return '未知（未保存或尚未读取）';if(!value.exists)return '此记录／分类不存在';
  if(value.value===null)return '未记录／空值';if(value.value==='')return '空字符串';
  if(key==='status')return ({not_started:'未游玩',playing:'游玩中',played:'已游玩',completed:'本次完成',on_hold:'搁置',dropped:'放弃'})[value.value]||String(value.value);
  if(key==='duration_minutes')return value.value+' 分钟';if(key.endsWith(':favorite'))return value.value?'已收藏':'未收藏';
  if(key.endsWith(':kind'))return value.value==='collection'?'集合':value.value==='tag'?'标签':String(value.value);return String(value.value);
 }
 function render(parent,rows,caption='字段对照 · 不自动合并'){
  const node=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;return e;};
  parent.replaceChildren(node('h4',caption));parent.className='field-comparison';
  if(!rows.length){parent.append(node('p','在已知基准中未发现字段变化；未读取的Web值仍属未知。'));return;}
  for(const row of rows.slice(0,200)){
   const values=[format(row.original,row.key),format(row.current,row.key),format(row.incoming,row.key)],block=node('details');
   block.open=values.every(v=>v.length<=160&&v.split('\n').length<=3);
   block.append(node('summary',row.label+' · '+(!row.original.known?'基准未知，请人工核对':row.conflict?'双方修改不同':row.remoteChanged?'Web已改变':'本机修改')));
   for(const [index,label]of ['原值（编辑基准）','Web值（只读观测）','本机稿（原操作）'].entries()){block.append(node('strong',label),node('p',values[index]));}
   parent.append(block);
  }
  if(rows.length>200)parent.append(node('p','仅显示前200个字段；原操作与其他条目保持。'));
 }
 return {play,item,organization,outcome,format,render};
});
