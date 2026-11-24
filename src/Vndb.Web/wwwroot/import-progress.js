(function(root,factory){const value=factory();if(typeof module==='object'&&module.exports)module.exports=value;else root.VnImportProgress=value;})(globalThis,function(){
 'use strict';
 function create({container,kind,action,pending,refresh}){
  const rows=new Map(),node=(tag,text,cls)=>VnTools.node(tag,text,cls);
  const text=(element,value)=>{if(element.textContent!==String(value))element.textContent=value;};
  function card(initial,preview=false){
   const root=node('article',undefined,'manager-block'),heading=node('h3'),state=node('p',undefined,'tool-state'),summary=node('p',undefined,'small');
   const result=node('details'),resultHeading=node('summary'),items=node('div'),proof=node('details'),proofText=node('pre'),controls=node('div'),retained=node('p',undefined,'small');
   root.dataset.progressId=initial.id;result.append(resultHeading,items);proof.append(node('summary',kind==='batch'?'批次ID、摘要与原回执':'任务及候选证明'),proofText);
   root.append(heading,state,summary);if(kind==='batch')root.append(result);root.append(proof,controls,retained);retained.hidden=true;
   const itemRows=new Map();let actionSignature=null,current=initial;
   function update(value,force=false){
    if(force)actionSignature=null;
    current=value;retained.hidden=true;
    text(heading,kind==='batch'?(preview?'文件预览':'导入批次')+' · '+VnTools.label(value.state):value.title);
    text(state,VnTools.label(value.state));text(summary,kind==='batch'?(value.accepted?'Web已接收；':'尚未提交；')+'处理 '+value.completed+' / '+value.total+' 项。处理结束仍可能含候选或失败。':'尝试 '+value.attempts+(value.error?' · '+value.error:''));
    text(proofText,JSON.stringify(value,null,2));
    if(kind==='batch'){
     text(resultHeading,'逐项结果（'+value.items.length+'）');const seen=new Set();
     for(const item of value.items){const key=String(item.index);seen.add(key);let row=itemRows.get(key);
      if(!row){row={root:node('div',undefined,'tool-row'),title:node('strong'),state:node('p',undefined,'tool-state'),message:node('p',undefined,'small')};row.root.append(row.title,row.state,row.message);items.append(row.root);itemRows.set(key,row);}
      text(row.title,(item.index+1)+'. '+item.title);text(row.state,VnTools.label(item.status));text(row.message,item.message||'');
     }
     for(const [key,row]of itemRows)if(!seen.has(key)){row.root.remove();itemRows.delete(key);}
    }
    const slot=kind+'/'+value.id;
    const actions=kind==='batch'?(preview&&value.state==='preview'?[['确认提交此批次','commit']]:['queued','processing'].includes(value.state)?[['暂停导入','pause']]:value.state==='paused'?[['继续导入','resume']]:[]):
     value.state==='paused'?[['继续补全','resume']]:['queued','working'].includes(value.state)?[['暂停补全','pause']]:['error','not_found'].includes(value.state)?[['重试补全','retry']]:[];
    const signature=JSON.stringify(actions);
    // Keep the exact original operation and focused button even if the durable job advances.
    if(pending(slot)||controls.contains(document.activeElement))return;
    if(!force&&signature===actionSignature)return;actionSignature=signature;controls.replaceChildren();
    for(const [caption,command]of actions)action(controls,caption,slot,(kind==='batch'?'/imports/':'/metadata/jobs/')+value.id+'/'+command,null,async r=>{actionSignature=null;await refresh(r);},
     r=>r.id===value.id&&(kind!=='batch'||Array.isArray(r.items)&&r.total===value.total));
   }
   update(initial);return {root,update,retained,slot:kind+'/'+initial.id,get current(){return current;}};
  }
  let force=false;
  return {card(value,preview){return card(value,preview).root;},invalidateActions(){force=true;},update(values){
   const seen=new Set();let index=0;
   for(const value of values){seen.add(value.id);let row=rows.get(value.id);if(!row){row=card(value);rows.set(value.id,row);}else row.update(value,force);
    if(container.children[index]!==row.root)container.insertBefore(row.root,container.children[index]||null);index++;
   }
   for(const [id,row]of rows)if(!seen.has(id)){
    if(pending(row.slot)||row.root.contains(document.activeElement)){row.retained.hidden=false;text(row.retained,'条目已不在当前列表；保留原操作，确认结果后再刷新。');}
    else{row.root.remove();rows.delete(id);}
   }
   let empty=container.querySelector('.progress-empty');if(!rows.size){if(!empty){empty=node('p',kind==='batch'?'尚无导入批次。':'没有联网补全任务。','manager-empty progress-empty');container.append(empty);}}else empty?.remove();
   force=false;
  }};
 }
 return {create};
});
