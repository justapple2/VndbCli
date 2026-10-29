'use strict';
(() => {
  const labels={pending:'待补全',matched:'已关联资料',needs_review:'待确认候选',not_found:'暂无匹配',error:'获取失败'};
  let page=1, more=false, running=false, sequence=0;
  function progress(message,error=false){const box=$('catalog-progress');box.textContent=message;box.hidden=!message;box.className='notice'+(error?' error':'');}
  async function refresh(){
    const current=++sequence;
    const params=new URLSearchParams({page,limit:20,q:$('catalog-query').value.trim()});
    if($('catalog-scope').value)params.set('scope',$('catalog-scope').value);
    if($('catalog-status').value)params.set('status',$('catalog-status').value);
    try{
      const [data,summary]=await Promise.all([api('/api/catalog?'+params),api('/api/catalog/stats')]);
      if(current!==sequence)return;
      $('catalog-summary').textContent=`${summary.titles} 个标题 · 已关联 ${summary.linked} · 待补全 ${summary.pending} · 待确认 ${summary.needs_review} · 暂无匹配 ${summary.not_found} · 获取失败 ${summary.errors} ｜ ${summary.play_records} 次游玩 · ${summary.local_directories+summary.archive_locations} 条本地副本标记`;
      $('catalog-list').replaceChildren();more=data.more;
      for(const row of data.results)$('catalog-list').append(card(row));
      if(!data.results.length)$('catalog-list').append(node('p','form-help','没有符合筛选条件的记录。'));
      $('catalog-previous').disabled=page===1;$('catalog-next').disabled=!more;$('catalog-page').textContent=`第 ${page} 页 · ${data.total} 个标题`;
    }catch(error){if(current===sequence)progress('读取失败，最近结果保留。'+VnTools.failure(error),true);}
  }
  function card(row){
    const s=row.source, wrap=node('article','catalog-card');
    const heading=node('div','catalog-card-heading');heading.append(node('h3','',s.title),node('span','play-status '+(row.status==='matched'?'completed':''),labels[row.status]));wrap.append(heading);
    const kinds=[];if(s.play_ids.length)kinds.push(`玩过 ${s.play_ids.length} 次`);if(s.local_ids.length)kinds.push(`本地副本 ${s.local_ids.length}`);if(s.archive_ids.length)kinds.push(`历史存在标记 ${s.archive_ids.length}`);
    if(s.item_ids?.length)kinds.push(`本地有副本 ${s.item_ids.length} 项`);
    wrap.append(node('p','form-help',kinds.join(' · ')));
    for(const id of s.novel_ids){const view=node('button','plain','查看作品资料 '+id+' ↗');view.onclick=()=>detail(id);wrap.append(view);}
    if(row.status!=='matched'){
      const search=node('div','catalog-candidate-search');const input=node('input');input.value=row.lookup?.query||s.title;input.maxLength=200;input.setAttribute('aria-label','检索标题：'+s.title);
      const button=node('button','secondary','查找候选');button.onclick=async()=>{
        button.disabled=true;progress('正在查询 '+s.title+'…');
        try{await api('/api/catalog/search',{post:true,body:{key:s.key,query:input.value.trim()}});progress('候选已保存，请核对标题、会社与版本。');await refresh();}
        catch(error){progress(error.message,true);}finally{button.disabled=false;}
      };search.append(input,button);wrap.append(search);
      if(row.lookup?.error)wrap.append(node('p','form-help',row.lookup.error));
      if(row.lookup?.truncated)wrap.append(node('p','form-help','候选较多，请补全标题或版本后重新检索；当前不会自动关联。'));
      for(const candidate of row.lookup?.candidates||[]){
        const item=node('div','catalog-candidate');const text=node('div');
        text.append(node('strong','',candidate.original||candidate.title),node('small','',`${candidate.id} · ${candidate.title} · ${candidate.developers.map(company).join(' / ')} · ${candidate.released||'日期未知'}`));
        const choose=node('button','secondary','收录并关联');choose.onclick=async()=>{
          choose.disabled=true;progress('正在收录资料并关联记录…');
          try{await api('/api/catalog/resolve',{post:true,body:{key:s.key,novel_id:candidate.id}});progress('作品、会社和封面已收录，个人记录与本地存在条目已关联。');await refresh();await Promise.all([stats(),load()]);}
          catch(error){progress(error.message,true);}finally{choose.disabled=false;}
        };item.append(text,choose);wrap.append(item);
      }
    }
    return wrap;
  }
  let backgroundJob=null,pollTimer,jobSequence=0;
  async function jobStatus(){
    const read=++jobSequence,previous=backgroundJob,result=await api('/api/catalog/jobs/latest');if(read!==jobSequence)return;backgroundJob=result;
    const job=backgroundJob;
    $('catalog-stop').hidden=!job||!['queued','running'].includes(job.status);$('catalog-resume').hidden=job?.status!=='paused';
    $('catalog-job-export').hidden=!job;
    if(!job){$('catalog-job-status').textContent='暂无后台任务。';return;}
    $('catalog-job-export').href='/api/catalog/jobs/'+job.id+'/export';
    $('catalog-job-status').textContent=`${({queued:'等待处理',running:'正在查询',paused:'已暂停',completed:'已完成'})[job.status]||job.status} · ${job.completed} / ${job.total} 个标题 · 间隔 ${job.interval_seconds} 秒 · 已关联 ${job.matched} · 待确认 ${job.needs_review} · 暂无匹配 ${job.not_found} · 失败 ${job.errors}${job.current_title?' ｜ 当前/下一个：'+job.current_title:''}${job.attempts?' ｜ 尝试 '+job.attempts+' / 3':''}${job.last_error?' ｜ '+job.last_error:''}`;
    if(previous&&previous.id===job.id&&previous.completed!==job.completed)await Promise.all([refresh(),stats()]);
  }
  async function poll(){
    if(!$('catalog-dialog').open)return;
    try{await jobStatus();}catch(error){progress('进度读取失败，最近进度保留。'+VnTools.failure(error),true);}
    if($('catalog-dialog').open)pollTimer=setTimeout(poll,3000);
  }
  async function run(){
    if(running)return;running=true;$('catalog-run').disabled=true;
    try{
      const job=await api('/api/catalog/jobs',{post:true,body:{interval_seconds:Number($('catalog-interval').value),scope:$('catalog-scope').value||null}});
      progress(job?'任务已保存到 NAS。关闭页面后继续执行，可暂停、继续或导出任务 JSON。':'没有待补全的标题。');
      await Promise.all([refresh(),jobStatus()]);
    }catch(error){progress(error.message,true);}finally{running=false;$('catalog-run').disabled=false;}
  }
  async function control(paused){
    if(!backgroundJob)return;
    try{await api('/api/catalog/jobs/'+backgroundJob.id+(paused?'/pause':'/resume'),{post:true});progress(paused?'已保存暂停请求，当前条目完成后停止。':'后台任务已继续。');await jobStatus();}
    catch(error){progress(error.message,true);}
  }
  async function open(){page=1;progress('');if(!$('catalog-dialog').open)$('catalog-dialog').showModal();clearTimeout(pollTimer);await Promise.all([refresh(),jobStatus()]);pollTimer=setTimeout(poll,3000);}
  for(const id of ['catalog-nav','catalog-open'])$(id).onclick=event=>{event.preventDefault();open();};
  $('catalog-close').onclick=()=>$('catalog-dialog').close();$('catalog-dialog').addEventListener('close',()=>{clearTimeout(pollTimer);load();});
  $('catalog-run').onclick=run;$('catalog-stop').onclick=()=>control(true);$('catalog-resume').onclick=()=>control(false);
  $('catalog-filter').onclick=()=>{page=1;refresh();};
  for(const id of ['catalog-scope','catalog-status'])$(id).onchange=()=>{page=1;refresh();};
  $('catalog-query').onkeydown=event=>{if(event.key==='Enter'){event.preventDefault();page=1;refresh();}};
  $('catalog-previous').onclick=()=>{if(page>1){page--;refresh();}};$('catalog-next').onclick=()=>{if(more){page++;refresh();}};
  if(new URLSearchParams(location.search).has('catalog'))open();
})();
