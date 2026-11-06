/* Official dataset status and explicit actions. Scheduling and all upstream I/O live in Library. */
globalThis.VnDatasetSync=(()=>{
 'use strict';
 function create({api,changed}){
  const $=id=>document.getElementById(id),writer=VnTools.writes(api),reads=VnTools.latest();
  let supported=false,status=null,basis=null,dirty=false,draftVersion=0,settingsSignature=null;
  const pending=slot=>writer.peek('dataset-sync/'+slot),date=v=>v?new Date(v).toLocaleString():'尚无记录';
  function validate(s){
   if(!s||!Number.isSafeInteger(s.settings?.revision)||s.settings.revision<0||typeof s.settings.enabled!=='boolean'||
    !Number.isInteger(s.settings.interval_days)||s.settings.interval_days<1||s.settings.interval_days>30||typeof s.worker_enabled!=='boolean'||
    s.latest_snapshot_date!=null&&!/^\d{4}-\d{2}-\d{2}$/.test(s.latest_snapshot_date))throw Error('同步状态格式无效，已有显示保留。');
   return s;
  }
  function settingsFrom(s){basis=s.settings;$('dataset-sync-enabled').checked=basis.enabled;$('dataset-sync-days').value=String(basis.interval_days);dirty=false;}
  function renderOperations(force=false){
   if(pending('check')||pending('update'))return;
   if(!force&&$('dataset-sync-actions').children.length)return;
   $('dataset-sync-actions').replaceChildren();
   VnTools.action($('dataset-sync-actions'),{label:'检查最新版',slot:'dataset-sync/check',path:'/datasets/sync/check',
    validate:r=>!!r?.settings,refresh:async r=>{validate(r);$('dataset-sync-outcome').textContent=r.error?'检查未完成；上次发现信息保留。':'已读取检查结果；上次检查后的官方变化仍需后续检查。';await refresh();renderOperations(true);}},writer);
   VnTools.action($('dataset-sync-actions'),{label:'检查并更新',slot:'dataset-sync/update',path:'/datasets/sync/update',
    validate:r=>['queued','existing','current','blocked','checking','check_failed','not_checked'].includes(r?.state),refresh:async r=>{
     $('dataset-sync-outcome').textContent=({queued:'已建立后台导入任务；尚未完成。',existing:'已有相同导入任务；继续查看原任务进度。',current:'已导入最近检查到的官方快照。',blocked:'有活动、暂停或失败任务；请先处理原任务。',checking:'另一次检查正在进行，请稍后读取状态。',check_failed:'检查未完成，未建立新的导入任务。',not_checked:'尚无有效官方日期，未建立任务。'})[r.state];
     await refresh();await changed?.();renderOperations(true);}},writer);
  }
  function renderSettingsAction(){
   if(!basis||pending('settings'))return;
   const body={expected_revision:basis.revision,enabled:$('dataset-sync-enabled').checked,interval_days:Number($('dataset-sync-days').value)};
   const signature=JSON.stringify(body);if(signature===settingsSignature&&$('dataset-sync-settings-actions').children.length)return;settingsSignature=signature;
   $('dataset-sync-settings-actions').replaceChildren();
   if(!Number.isInteger(body.interval_days)||body.interval_days<1||body.interval_days>30){$('dataset-sync-settings-actions').textContent='间隔须为1–30天。';return;}
   VnTools.action($('dataset-sync-settings-actions'),{label:'保存定时同步设置',slot:'dataset-sync/settings',path:'/datasets/sync/settings',body,
    validate:r=>r?.revision===body.expected_revision+1&&r.enabled===body.enabled&&r.interval_days===body.interval_days,
    refresh:async saved=>{
     const edited=$('dataset-sync-enabled').checked!==body.enabled||Number($('dataset-sync-days').value)!==body.interval_days;
     basis=saved;dirty=edited;$('dataset-sync-outcome').textContent=saved.enabled?'定时同步已启用；已有任务仍按原状态处理。':'定时同步已关闭；已接收的导入任务可在下方单独暂停。';
     await refresh();renderSettingsAction();}},writer);
  }
  async function refresh(resetSettings=false){
   if(!supported)return;const current=reads.next(),draft=draftVersion;
   if(resetSettings&&pending('settings')&&!['conflict','rejected'].includes(pending('settings').state)){$('dataset-sync-outcome').textContent='设置原操作结果未确认，请按原ID重试；原稿保留。';return;}
   try{
    const s=validate(await api('/datasets/sync',{method:'GET'}));if(!current())return;status=s;
    if(resetSettings&&draft===draftVersion){writer.reload();settingsFrom(s);}
    else if(!dirty&&!pending('settings'))settingsFrom(s);
    $('dataset-sync-status').textContent=(s.checking?'正在检查官方最新日期':s.error?'上次检查未完成':s.update_available===true?'发现可导入的新快照':s.update_available===false?'已导入最近检查到的快照':'尚未检查官方最新快照')+(!s.worker_enabled?' · 后台执行未启用':'');
    const facts=[['完整导入快照',s.completed_snapshot_at?.slice(0,10)||'尚未完成导入'],['官方最新（上次检查）',s.latest_snapshot_date||'未知'],['最近成功检查',date(s.last_checked_at)],['下次允许检查',date(s.check_not_before)],['自动同步',s.settings.enabled?'每 '+s.settings.interval_days+' 天':'关闭'],['下次自动检查',s.settings.enabled?date(s.next_check_at):'未启用']];
    if(s.active_job)facts.push(['现有任务',s.active_job.source+' · '+VnTools.label(s.active_job.state)]);
    if(s.error)facts.push(['检查说明',s.error]);
    if(dirty&&basis?.revision!==s.settings.revision)facts.push(['设置待核对','Web设置已改变；未保存的表单保留，可明确重读后编辑。']);
    const block=$('dataset-sync-facts');block.replaceChildren();for(const [label,value]of facts)block.append(VnTools.node('p',label+'：'+value,'small'));
    $('dataset-sync-settings').disabled=false;renderOperations();renderSettingsAction();
    if(writer.archived.length){$('dataset-sync-history').replaceChildren();VnTools.details($('dataset-sync-history'),'重读前保留的原操作',writer.archived.map(e=>({id:e.id,state:e.state,body:JSON.parse(e.json)})));}
   }catch(e){if(current())$('dataset-sync-status').textContent='同步状态读取失败，已有内容和设置草稿保留。'+VnTools.failure(e);}
  }
  for(const id of ['dataset-sync-enabled','dataset-sync-days'])$(id).onchange=()=>{dirty=true;draftVersion++;renderSettingsAction();};
  $('dataset-sync-refresh').onclick=()=>refresh();$('dataset-sync-reload-settings').onclick=()=>refresh(true);
  return {refresh,activate(value){supported=value===true;$('dataset-sync-settings').disabled=!supported;if(supported)return refresh();$('dataset-sync-status').textContent='当前Web未声明自动同步能力；仍可使用下方按日期导入。';},get unsettled(){return writer.unsettled;},get status(){return status;}};
 }
 return {create};
})();
