'use strict';
(() => {
  const $=id=>document.getElementById(id), node=(tag,text)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
  const labels={unlinked:'未关联VNDB',metadata_missing:'公开资料缺失',company_missing:'会社待补全',cover_missing:'封面待补全',catalog_needs_review:'候选待确认',catalog_not_found:'暂未找到',catalog_pending:'等待补全',catalog_error:'补全失败',path_missing:'上次核对路径失效',observation_needed:'安装待核对',components_missing:'安装未关联组件',component_item_removed:'关联条目已移除',legacy_unmapped:'旧位置待映射'};
  const statuses={present:'核对存在',missing:'目录缺失',unverified:'完整组件待核对',unknown:'未知，保持原状态'};
  const launches={not_configured:'尚未绑定启动配置',configured_not_run:'已有配置，未运行验证',profile_path_missing:'已保存程序路径失效',inspection_unavailable:'无法读取启动线索'};
  let page=1, more=false, report=null, key=(()=>{try{return sessionStorage.getItem('library-key')||'';}catch{return '';}})(), sequence=0;
  function notice(text){$('notice').textContent=text;$('notice').hidden=!text;}
  const api=(path,body)=>VnTools.request('/api/v1'+path,{method:body===undefined?'GET':'POST',body},key);
  async function load(){const current=++sequence;const params=new URLSearchParams({page,page_size:30});if($('device').value)params.set('device_id',$('device').value);if($('kind').value)params.set('kind',$('kind').value);if($('query').value.trim())params.set('q',$('query').value.trim());let result;try{result=await api('/quality?'+params);}catch(e){if(current===sequence)notice('读取失败，保留最近成功显示。'+VnTools.failure(e));return;}if(current!==sequence)return;
    $('summary').textContent=Object.entries(result.counts).map(([kind,count])=>(labels[kind]||kind)+' '+count).join(' · ')||'当前未发现资料或安装观测缺口。启动配置请结合本机报告核对。';
    const selected=$('kind').value;$('kind').replaceChildren(new Option('全部问题',''));for(const [kind,count] of Object.entries(result.counts))$('kind').add(new Option((labels[kind]||kind)+' ('+count+')',kind));$('kind').value=selected;
    $('issues').replaceChildren();for(const issue of result.items){const card=node('article');card.className='manager-block';card.append(node('h3',issue.title),node('p',(labels[issue.kind]||issue.kind)+' · '+issue.message));if(issue.novel_id)card.append(node('p',issue.novel_id));const action=node('a',issue.action==='catalog'?'核对候选':issue.action==='installations'?'查看安装／组件':issue.action==='query'?'查看公开资料':'管理条目');action.href=issue.action==='catalog'?'/?catalog=1':issue.action==='installations'?'/installations.html':issue.action==='query'?'/?vn='+encodeURIComponent(issue.novel_id):'/manage.html';card.append(action);
      if(issue.action==='query'&&/^v\d+$/.test(issue.novel_id)){const button=node('button','联网补全此条资料');button.className='secondary';button.onclick=()=>perform(async()=>{button.disabled=true;try{await api('/vns/'+issue.novel_id+'/refresh',{});await load();notice('公开资料已更新；个人记录保持原样。');}finally{button.disabled=false;}});card.append(button);} $('issues').append(card);}
    more=result.more;$('previous').disabled=page===1;$('next').disabled=!more;$('page-label').textContent=`第 ${page} 页 · ${result.total} 个问题`;}
  async function perform(action){try{await action();}catch(error){notice(VnTools.failure(error));}}
  $('filters').onsubmit=e=>{e.preventDefault();page=1;perform(load);};$('previous').onclick=()=>{if(page>1){page--;perform(load);}};$('next').onclick=()=>{if(more){page++;perform(load);}};
  $('key-open').onclick=()=>{$('key').value=key;$('key-dialog').showModal();};$('key-close').onclick=()=>$('key-dialog').close();$('key-form').onsubmit=e=>{e.preventDefault();key=$('key').value.trim();try{sessionStorage.setItem('library-key',key);}catch{}$('key-dialog').close();};
  perform(async()=>{for(const device of await api('/devices'))if(!device.mapped_to_id)$('device').add(new Option(device.details.name+(device.legacy?'（历史设备）':''),device.id));await load();});
})();
