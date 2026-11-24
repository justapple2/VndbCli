(function(root,factory){const value=factory();if(typeof module==='object'&&module.exports)module.exports=value;else root.VnDatasetMaintenance=value;})(globalThis,function(){
 'use strict';
 const kinds={unreferenced_input:'无引用临时输入',unreferenced_stage:'无引用预检暂存',completed_stage:'已完成且可重建的暂存'};
 function bytes(value){return Number.isFinite(value)&&value>=0?(value/1024/1024/1024).toFixed(2)+' GiB':'未知';}
 function validatePreview(p){return !!p&&/^[a-f0-9]{64}$/.test(p.id)&&/^[a-f0-9]{64}$/.test(p.sha256)&&Array.isArray(p.entries)&&p.entries.every(e=>kinds[e.kind]&&Number.isSafeInteger(e.bytes)&&e.bytes>=0);}
 function create({api}){
  const $=id=>document.getElementById(id),writer=VnTools.writes(api),reads=VnTools.latest();let supported=false,preview=null;
  const pending=()=>['dataset-maintenance/preview','dataset-maintenance/cleanup'].some(slot=>writer.peek(slot));
  function renderPreviewAction(){
   if(pending())return;$('dataset-cleanup-preview-action').replaceChildren();
   VnTools.action($('dataset-cleanup-preview-action'),{label:'生成清理预览',slot:'dataset-maintenance/preview',path:'/datasets/maintenance/preview',validate:validatePreview,
    refresh:async p=>{preview=p;renderPlan();}},writer);
  }
  function renderPlan(){
   const list=$('dataset-cleanup-plan');list.replaceChildren();$('dataset-cleanup-actions').replaceChildren();
   list.append(VnTools.node('p','预计释放 '+bytes(preview.reclaimable_bytes)+'；共 '+preview.entries.length+' 项。原官方包、活动输入、数据库、封面及恢复资料保留。','small'));
   for(const e of preview.entries)list.append(VnTools.node('p',kinds[e.kind]+' · '+bytes(e.bytes)+(e.job_id?' · 任务 '+e.job_id:''),'small'));
   if(!preview.entries.length){renderPreviewAction();return;}
   VnTools.action($('dataset-cleanup-actions'),{label:'确认清理以上暂存',slot:'dataset-maintenance/cleanup',path:'/datasets/maintenance/cleanup',body:{preview_id:preview.id,sha256:preview.sha256},
    validate:r=>r?.preview_id===preview.id&&Number.isSafeInteger(r.removed)&&r.removed>=0,
    refresh:async r=>{list.replaceChildren(VnTools.node('p','清理已确认：'+r.removed+' 项 · '+bytes(r.released_bytes),'notice'));preview=null;renderPreviewAction();await refresh();}},writer);
  }
  async function refresh(){
   if(!supported)return;const current=reads.next();
   try{
    const s=await api('/datasets/maintenance',{method:'GET'});if(!current())return;
    if(!s||!Number.isFinite(s.available_bytes)||!Number.isFinite(s.input_bytes)||!Number.isFinite(s.stage_bytes))throw Error('容量摘要无效。');
    $('dataset-maintenance-status').textContent='数据卷可用 '+bytes(s.available_bytes)+' · 原包／输入 '+bytes(s.input_bytes)+'（'+s.input_count+'） · 暂存 '+bytes(s.stage_bytes)+'（'+s.stage_count+'） · 完整快照 '+(s.completed_snapshot_at?.slice(0,10)||'尚未完成')+'。'+s.wait_reason;
   }catch(e){if(current())$('dataset-maintenance-status').textContent='容量读取失败；已有预览保留。'+VnTools.failure(e);}
  }
  $('dataset-cleanup-reload').onclick=()=>{
   if(['dataset-maintenance/preview','dataset-maintenance/cleanup'].some(slot=>['uncertain','retryable','sending','ready'].includes(writer.peek(slot)?.state))){$('dataset-maintenance-status').textContent='原操作结果待确认，请使用原操作重试；保留当前预览。';return;}
   writer.reload();preview=null;$('dataset-cleanup-plan').replaceChildren();$('dataset-cleanup-actions').replaceChildren();renderPreviewAction();refresh();
  };
  return {refresh,activate(value){supported=value===true;$('dataset-maintenance-controls').hidden=!supported;if(supported){renderPreviewAction();return refresh();}$('dataset-maintenance-status').textContent='当前 Web 未提供容量与清理能力。';},get unsettled(){return writer.unsettled;}};
 }
 return {create,bytes,validatePreview};
});
