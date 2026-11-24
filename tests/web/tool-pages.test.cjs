// Run the actual page scripts against synthetic DOM/API adapters, with no browser or network.
const {test}=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs'),path=require('node:path');
const root=path.join(__dirname,'../../src/Vndb.Web/wwwroot');
class Element {
 constructor(tag='div'){this.tagName=tag.toUpperCase();this.children=[];this.dataset={};this.hidden=false;this.disabled=false;this._text='';this._value=undefined;this.classList={toggle(){},add(){},remove(){}};}
 append(...nodes){for(const n of nodes){if(n.tagName==='#FRAGMENT'){this.append(...n.children);continue;}n.parentNode=this;this.children.push(n);}}
 insertBefore(node,before){if(node.parentNode){const i=node.parentNode.children.indexOf(node);if(i>=0)node.parentNode.children.splice(i,1);}const i=before?this.children.indexOf(before):-1;node.parentNode=this;this.children.splice(i<0?this.children.length:i,0,node);return node;}
 remove(){if(this.parentNode){const i=this.parentNode.children.indexOf(this);if(i>=0)this.parentNode.children.splice(i,1);this.parentNode=null;}}
 prepend(...nodes){this.children.unshift(...nodes);}
 replaceChildren(...nodes){this.children=[];this._text='';this.append(...nodes);}
 set textContent(v){this._text=String(v??'');this.children=[];} get textContent(){return this._text+this.children.map(n=>n.textContent).join(' ');}
 set value(v){this._value=String(v??'');}get value(){return this._value??this.children.find(n=>n.tagName==='OPTION')?.value??'';}
 setAttribute(k,v){this[k]=v;}addEventListener(){} showModal(){this.open=true;}close(){this.open=false;}
 add(option){this.append(option);}
 querySelectorAll(selector){const match=n=>selector==='button'?n.tagName==='BUTTON':selector==='details'?n.tagName==='DETAILS':selector.startsWith('.')?(n.className||'').split(' ').includes(selector.slice(1)):false;return this.children.flatMap(n=>[...(match(n)?[n]:[]),...n.querySelectorAll(selector)]);}
 querySelector(s){return this.querySelectorAll(s)[0]||null;}
 contains(node){return this===node||this.children.some(child=>child.contains(node));}
}
const drain=async()=>{for(let i=0;i<12;i++)await new Promise(resolve=>setImmediate(resolve));};
function page(name,route,tab='imports',personalOrganization=false){
 const html=fs.readFileSync(path.join(root,name+'.html'),'utf8'),elements={};for(const m of html.matchAll(/<(\w+)[^>]*\bid="([^"]+)"/g))elements[m[2]]=new Element(m[1]);
 const tabs=['shelf','imports','review','trash'].map(value=>{const e=new Element('button');e.dataset.tab=value;return e;}),calls=[],intervals=[];
 const document={getElementById:id=>{assert.ok(elements[id],`Actual ${name}.html must declare ${id}`);return elements[id];},createElement:tag=>new Element(tag),createDocumentFragment:()=>new Element('#fragment'),querySelectorAll:s=>s==='[data-tab]'?tabs:[],visibilityState:'visible'};
 const context={document,URLSearchParams,URL,AbortSignal,crypto:require('node:crypto').webcrypto,structuredClone,console,location:{search:'?tab='+tab},scrollY:0,setTimeout,clearTimeout,setInterval:fn=>{intervals.push(fn);return 1;},sessionStorage:{getItem:()=>'',setItem(){}},addEventListener(){},VnShell:{setView(){}},
  fetch:async(url,options={})=>{const call={url,method:options.method||'GET',body:options.body?JSON.parse(options.body):undefined,key:options.headers?.['Idempotency-Key']};calls.push(call);const r=await route(call);return {ok:!r.status||r.status<400,status:r.status||200,json:async()=>r.data??r};}};
 context.Option=function(text,value){const e=new Element('option');e.textContent=text;e.value=value;return e;};context.window=context;context.VnPersonal={create:()=>({open(){}})};context.VnView={element:(tag,cls,text)=>{const e=new Element(tag);e.className=cls;e.textContent=text;return e;},storage:()=>({get:()=>null,set(){}}),store:(_id,defaults)=>({read:()=>({...defaults}),save(){}}),latest:()=>({cancel(){},begin(){return {current:()=>true};}}),appearance(){},select(){},failure:e=>e.message,restoreScroll(){}};
 vm.createContext(context);
 const adapter=context.VnView;vm.runInContext(fs.readFileSync(path.join(root,'library-view.js'),'utf8'),context,{filename:'library-view.js'});
 context.VnView={...adapter,card:context.VnView.card,itemBadges:context.VnView.itemBadges,progressLabel:context.VnView.progressLabel,historyControl:context.VnView.historyControl,shelfQuickViews:context.VnView.shelfQuickViews,personalCompany:context.VnView.personalCompany};
 for(const file of ['tool-feedback.js','import-input.js','vn-details.js','releases.js',...(personalOrganization?['personal-organization.js']:[]),...(name==='manage'?['dataset-sync.js','import-progress.js']:[]),name+'.js'])vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context,{filename:file});
 return {elements,tabs,calls,intervals,context,buttons:id=>elements[id].querySelectorAll('button'),text:id=>elements[id].textContent};
}
const info={import_limits:{maximum_bytes:1048576,maximum_items:500}};
const batch=(id='import-original')=>({id,state:'queued',accepted:true,total:1,completed:0,items:[{index:0,title:'合成タイトル',status:'preview'}]});

test('import polling updates keyed text while retaining expanded results, focus and original uncertain writes',async()=>{
 let complete=false,lose=true;const current=()=>({...batch(),state:complete?'complete':'queued',completed:complete?1:0});
 const p=page('manage',async c=>{
  if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/imports'))return [current()];if(c.url.endsWith('/metadata/jobs'))return [];
  if(c.url.endsWith('/pause')){complete=true;if(lose)throw TypeError('synthetic lost reply');return current();}throw Error(c.url);
 });await drain();
 const original=p.elements.batches.children[0],details=original.querySelector('details'),button=p.buttons('batches').find(b=>b.textContent==='暂停导入');details.open=true;p.context.document.activeElement=button;
 p.intervals[0]();await drain();assert.equal(p.elements.batches.children[0],original);assert.equal(original.querySelector('details'),details);assert.equal(details.open,true);assert.equal(p.buttons('batches').find(b=>b.textContent==='暂停导入'),button);
 await button.onclick();p.intervals[0]();await drain();assert.match(p.text('batches'),/处理 1 \/ 1/);assert.equal(details.open,true);
 const retry=p.buttons('batches').find(b=>b.textContent==='按原操作重试');assert.equal(retry.hidden,false);lose=false;await retry.onclick();
 const writes=p.calls.filter(c=>c.method==='POST');assert.equal(writes.length,2);assert.deepEqual(writes[0],writes[1]);
 p.context.document.activeElement=null;p.intervals[0]();await drain();assert.equal(p.elements.batches.children[0],original);assert.equal(details.open,true);
 assert.equal(p.buttons('batches').some(b=>b.textContent==='暂停导入'),false);
});

test('slow import polling remains single-flight, hidden pages stay idle, and focus defers a state action change',async()=>{
 let delay=false,release,paused=false;const p=page('manage',async c=>{
  if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/metadata/jobs'))return [];
  if(c.url.endsWith('/imports')){if(delay)return new Promise(r=>release=r);return [{...batch(),state:paused?'paused':'queued'}];}throw Error(c.url);
 });await drain();
 const button=p.buttons('batches').find(b=>b.textContent==='暂停导入');p.context.document.activeElement=button;paused=true;
 p.intervals[0]();await drain();assert.equal(p.buttons('batches').find(b=>b.textContent==='暂停导入'),button);
 p.context.document.activeElement=null;p.intervals[0]();await drain();assert.ok(p.buttons('batches').find(b=>b.textContent==='继续导入'));
 const before=p.calls.filter(c=>c.url.endsWith('/imports')).length;delay=true;p.intervals[0]();await drain();for(let i=0;i<5;i++)p.intervals[0]();await drain();
 assert.equal(p.calls.filter(c=>c.url.endsWith('/imports')).length,before+1);release([batch()]);await drain();
 p.context.document.visibilityState='hidden';const count=p.calls.length;for(let i=0;i<5;i++)p.intervals[0]();await drain();assert.equal(p.calls.length,count);
});
const file=(name,data)=>({name,size:JSON.stringify(data).length,text:async()=>typeof data==='string'?data:JSON.stringify(data)});
const play={id:'play-a',details:{title:'合成作品',status:'played',started_on:null,duration_minutes:null},revision:3};
const time={play_id:'play-a',base_minutes:null,session_seconds:10,adjustment_seconds:0,total_seconds:null,pending_sessions:0,revision:3};

test('personal shelf handlers display server state and send filters before paging without creating records',async()=>{
 const item={id:'exact-item',details:{title:'合成个人条目',novel_id:'v17'},revision:1};
 const p=page('manage',async c=>{
  if(c.url.includes('/shelf?'))return {format:'vn-shelf-summary',format_version:1,items:[{item,metadata:null,play_count:0,state:{progress:{state:'unrecorded',statuses:[]},is_local:true}}],total:1,more:false};
  throw Error(c.url);
 },'shelf');await drain();
 assert.match(p.text('items'),/未记录/);assert.match(p.text('items'),/在本地/);
 p.elements['item-progress'].value='unplayed';p.elements['item-progress'].onchange();await drain();
 p.elements['item-presence'].value='true';p.elements['item-presence'].onchange();await drain();
 const query=new URL(p.calls.at(-1).url,'https://example.test').searchParams;
 assert.equal(query.get('progress'),'unplayed');assert.equal(query.get('is_local'),'true');assert.equal(query.get('page'),'1');
 assert.ok(p.calls.every(c=>c.method==='GET'));
 p.elements['clear-filters'].onclick();await drain();
 assert.equal(new URL(p.calls.at(-1).url,'https://example.test').searchParams.get('is_local'),null);
});
test('actual quick view and history controls preserve the explicit submit boundary',async()=>{
 const p=page('manage',async c=>{
  if(c.url.includes('/shelf?'))return {format:'vn-shelf-summary',format_version:1,items:[],total:0,more:false};throw Error(c.url);
 },'shelf');await drain();
 p.elements['item-quick'].value='owned_unplayed';p.elements['item-quick'].onchange();await drain();
 let query=new URL(p.calls.at(-1).url,'https://example.test').searchParams;
 assert.equal(query.get('progress'),'unplayed');assert.equal(query.get('is_local'),'true');
 p.elements['item-status'].value='playing';p.elements['item-status'].onchange();await drain();
 query=new URL(p.calls.at(-1).url,'https://example.test').searchParams;assert.equal(query.get('status'),'playing');
 p.elements['item-query'].value='Synthetic history';p.elements['item-search'].onsubmit({preventDefault(){}});await drain();
 const before=p.calls.length;p.elements['item-query'].value='New draft';
 p.buttons('item-history').find(b=>b.textContent==='回填，不查询').onclick();await drain();
 assert.equal(p.elements['item-query'].value,'Synthetic history');assert.equal(p.calls.length,before);
 p.buttons('item-history').find(b=>b.textContent==='清空历史').onclick();assert.ok(p.calls.every(c=>c.method==='GET'));
});

test('opening summary cards fetches full item before editing and preserves long notes',async()=>{
 const item={id:'exact-item',details:{title:'Synthetic',novel_id:'v17'},revision:1};
 const p=page('manage',async c=>{
  if(c.url.includes('/shelf?'))return {format:'vn-shelf-summary',format_version:1,items:[{item,metadata:null,play_count:0,state:{progress:{state:'unrecorded',statuses:[]},is_local:false}}],total:1,more:false};
  if(c.url.endsWith('/items/exact-item'))return {item:{...item,details:{...item.details,notes:'完整备注一',extra_notes:'完整备注二'}},metadata:{novel:{description:'完整简介'},developers:[]},play_count:0};
  if(c.url.includes('/plays?item_id='))return [];
  throw Error(c.url);
 },'shelf');await drain();
 const card=p.elements.items.children[0];
 // The actual card exposes the same open handler to click and keyboard users.
 const open=p.buttons('items').find(b=>/详情|编辑/.test(b.textContent));
 assert.ok(open);await open.onclick();await drain();
 assert.equal(p.elements['item-notes'].value,'完整备注一');assert.equal(p.elements['item-extra'].value,'完整备注二');
 assert.match(p.text('managed-details'),/完整简介/);assert.ok(p.calls.every(c=>c.method==='GET'));assert.ok(card);
});

test('official dataset confirmation retains accepted creation across a failed progress refresh',async()=>{
 let failRead=false;const job={id:'dataset-synthetic',source:'vndb-db-2026-10-27.tar.zst',revision:1,state:'queued',processed:0,total:0,written:0,kept:0};
 const p=page('manage',async c=>{
  if(c.url.endsWith('/info'))return info;
  if(c.url.endsWith('/imports')||c.url.endsWith('/metadata/jobs'))return [];
  if(c.url.endsWith('/datasets')&&c.method==='POST'){failRead=true;return job;}
  if(c.url.endsWith('/datasets'))return failRead?{status:500,data:{title:'synthetic read failure'}}:[job];
  throw Error(c.url);
 });await drain();p.elements['dataset-date'].value='2026-10-27';p.elements['dataset-create'].onclick();
 const confirm=p.buttons('dataset-attempts')[0];await confirm.onclick();
 assert.equal(p.calls.filter(c=>c.method==='POST').length,1);assert.equal(confirm.disabled,true);
 assert.match(p.text('datasets-summary'),/读取失败/);await p.elements['datasets-refresh'].onclick();
 assert.equal(p.calls.filter(c=>c.method==='POST').length,1);
 assert.deepEqual(p.calls.find(c=>c.method==='POST').body,{snapshot_date:'2026-10-27'});
});

const syncStatus=(patch={})=>({settings:{revision:0,enabled:false,interval_days:7},latest_snapshot_date:null,last_checked_at:null,last_attempt_at:null,
 check_not_before:null,next_check_at:null,error:null,checking:false,completed_snapshot_at:'2026-10-27T08:00:27Z',completed_at:'2026-10-29T00:05:00Z',active_job:null,update_available:null,worker_enabled:true,...patch});
const syncInfo={...info,capabilities:['dataset_sync']};
function syncPage(route){return page('manage',async c=>{
 if(c.url.endsWith('/info'))return syncInfo;
 if(c.url.endsWith('/imports')||c.url.endsWith('/metadata/jobs')||c.url.endsWith('/datasets'))return [];
 return route(c);
});}
test('dataset synchronization shows passive freshness and checks only on explicit action',async()=>{
 let state=syncStatus();const p=syncPage(async c=>{
  if(c.url.endsWith('/sync/check')){state=syncStatus({latest_snapshot_date:'2026-11-06',last_checked_at:'2026-11-06T12:00:00Z',update_available:true});return state;}
  if(c.url.endsWith('/sync'))return state;throw Error(c.url);
 });await drain();assert.equal(p.calls.filter(c=>c.method==='POST').length,0);assert.match(p.text('dataset-sync-facts'),/2026-10-27/);assert.match(p.text('dataset-sync-status'),/尚未检查/);
 assert.equal(p.elements['dataset-sync-enabled'].checked,false);assert.equal(p.elements['dataset-sync-days'].value,'7');
 await p.buttons('dataset-sync-actions').find(b=>b.textContent==='检查最新版').onclick();
 assert.match(p.text('dataset-sync-status'),/发现可导入/);assert.match(p.text('dataset-sync-facts'),/2026-11-06/);
 assert.equal(p.calls.filter(c=>c.method==='POST').length,1);assert.ok(p.calls.find(c=>c.method==='POST').url.endsWith('/sync/check'));
});
test('passive sync status polling preserves action nodes and keyboard focus targets',async()=>{
 const p=syncPage(async c=>{if(c.url.endsWith('/sync'))return syncStatus();throw Error(c.url);});await drain();
 const check=p.buttons('dataset-sync-actions')[0],save=p.buttons('dataset-sync-settings-actions')[0];
 await p.elements['dataset-sync-refresh'].onclick();assert.equal(p.buttons('dataset-sync-actions')[0],check);assert.equal(p.buttons('dataset-sync-settings-actions')[0],save);
 p.elements['dataset-sync-days'].value='14';p.elements['dataset-sync-days'].onchange();
 assert.notEqual(p.buttons('dataset-sync-settings-actions')[0],save);assert.equal(p.buttons('dataset-sync-actions')[0],check);
});
test('lost update response retains the exact operation across status refresh and input changes',async()=>{
 let lose=true;const accepted={state:'queued',snapshot_date:'2026-11-06',job:{id:'original-job'}};
 const p=syncPage(async c=>{
  if(c.url.endsWith('/sync'))return syncStatus({latest_snapshot_date:'2026-11-06',update_available:true});
  if(c.url.endsWith('/sync/update')){if(lose){lose=false;throw TypeError('Synthetic lost response');}return accepted;}throw Error(c.url);
 });await drain();await p.buttons('dataset-sync-actions').find(b=>b.textContent==='检查并更新').onclick();
 p.elements['dataset-date'].value='2026-11-05';await p.elements['dataset-sync-refresh'].onclick();
 const retry=p.buttons('dataset-sync-actions').find(b=>b.textContent==='按原操作重试'&&!b.hidden);assert.ok(retry);await retry.onclick();
 const writes=p.calls.filter(c=>c.method==='POST');assert.equal(writes.length,2);assert.deepEqual(writes[0],writes[1]);assert.match(p.text('dataset-sync-outcome'),/后台导入任务.*尚未完成/);
});
test('polling preserves dirty schedule fields and conflict reread keeps proof until the GET succeeds',async()=>{
 let state=syncStatus(),fail=false;const p=syncPage(async c=>{
  if(c.url.endsWith('/sync'))return fail?{status:500,data:{title:'Synthetic read failure'}}:state;
  if(c.url.endsWith('/sync/settings'))return {status:409,data:{title:'Synthetic revision conflict'}};throw Error(c.url);
 });await drain();p.elements['dataset-sync-enabled'].checked=true;p.elements['dataset-sync-enabled'].onchange();p.elements['dataset-sync-days'].value='3';p.elements['dataset-sync-days'].onchange();
 state=syncStatus({settings:{revision:1,enabled:false,interval_days:7}});await p.elements['dataset-sync-refresh'].onclick();
 assert.equal(p.elements['dataset-sync-days'].value,'3');assert.equal(p.elements['dataset-sync-enabled'].checked,true);
 await p.buttons('dataset-sync-settings-actions').find(b=>b.textContent==='保存定时同步设置').onclick();
 const original=p.calls.find(c=>c.method==='POST');assert.deepEqual(original.body,{expected_revision:0,enabled:true,interval_days:3});
 fail=true;await p.elements['dataset-sync-reload-settings'].onclick();assert.equal(p.elements['dataset-sync-days'].value,'3');assert.match(p.text('dataset-sync-settings-actions'),/原操作/);
 fail=false;await p.elements['dataset-sync-reload-settings'].onclick();assert.equal(p.elements['dataset-sync-days'].value,'7');assert.match(p.text('dataset-sync-history'),/expected_revision/);assert.equal(p.calls.filter(c=>c.method==='POST').length,1);
});
test('a matching status GET cannot acknowledge lost settings and later form edits do not replace its retry body',async()=>{
 let state=syncStatus(),lose=true;const p=syncPage(async c=>{
  if(c.url.endsWith('/sync'))return state;
  if(c.url.endsWith('/sync/settings')){state=syncStatus({settings:{revision:1,enabled:c.body.enabled,interval_days:c.body.interval_days}});if(lose){lose=false;throw TypeError('Synthetic response lost');}return state.settings;}throw Error(c.url);
 });await drain();p.elements['dataset-sync-enabled'].checked=true;p.elements['dataset-sync-days'].value='3';p.elements['dataset-sync-days'].onchange();
 await p.buttons('dataset-sync-settings-actions').find(b=>b.textContent==='保存定时同步设置').onclick();await p.elements['dataset-sync-refresh'].onclick();
 p.elements['dataset-sync-days'].value='14';p.elements['dataset-sync-days'].onchange();
 await p.buttons('dataset-sync-settings-actions').find(b=>b.textContent==='按原操作重试'&&!b.hidden).onclick();
 const writes=p.calls.filter(c=>c.method==='POST');assert.equal(writes.length,2);assert.deepEqual(writes[0],writes[1]);assert.equal(writes[1].body.interval_days,3);assert.equal(p.elements['dataset-sync-days'].value,'14');
});
test('late synchronization reads cannot replace newer status and inactive worker settings can still be disabled',async()=>{
 let delayed=false,release,state=syncStatus({worker_enabled:false,settings:{revision:1,enabled:true,interval_days:7}});
 const p=syncPage(async c=>{
  if(c.url.endsWith('/sync')){if(delayed)return new Promise(r=>release=r);return state;}
  if(c.url.endsWith('/sync/settings'))return {revision:2,enabled:false,interval_days:7};throw Error(c.url);
 });await drain();assert.equal(p.elements['dataset-sync-settings'].disabled,false);
 delayed=true;const old=p.elements['dataset-sync-refresh'].onclick();await drain();delayed=false;
 state=syncStatus({worker_enabled:false,settings:{revision:1,enabled:true,interval_days:7},latest_snapshot_date:'2026-11-06',update_available:true});await p.elements['dataset-sync-refresh'].onclick();
 release(syncStatus());await old;assert.match(p.text('dataset-sync-facts'),/2026-11-06/);
 p.elements['dataset-sync-enabled'].checked=false;p.elements['dataset-sync-enabled'].onchange();
 await p.buttons('dataset-sync-settings-actions').find(b=>b.textContent==='保存定时同步设置').onclick();assert.equal(p.calls.find(c=>c.method==='POST').body.enabled,false);
});

test('manage actual handlers retain every file result and successful progress through later read failure',async()=>{
 let fail=false;const p=page('manage',async c=>{
  if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/imports/preview'))return {...batch(),state:'preview',accepted:false};
  if(c.url.endsWith('/imports'))return fail?{status:500,data:{title:'synthetic read failure'}}:[batch()];if(c.url.endsWith('/metadata/jobs'))return [];throw Error(c.url);
 });await drain();assert.match(p.text('batches'),/等待处理/);
 p.elements['json-files'].files=[file('bad.json','{'),file('good.json',{items:[{}]})];await p.elements['preview-files'].onclick();await drain();
 assert.match(p.text('previews'),/bad.json/);assert.match(p.text('previews'),/格式/);assert.match(p.text('previews'),/good.json/);assert.match(p.text('previews'),/确认提交此批次/);
 assert.equal(p.calls.filter(c=>c.method==='POST').length,1);const previous=p.text('batches');fail=true;await p.elements['refresh-imports'].onclick();assert.equal(p.text('batches'),previous);assert.match(p.text('message'),/保留最近成功/);
});
test('manage late import refresh cannot replace view after navigation to candidates',async()=>{
 let release,delay=false;const p=page('manage',async c=>{if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/imports')){if(delay)return new Promise(r=>release=r);return [batch()];}if(c.url.endsWith('/metadata/jobs'))return [];if(c.url.endsWith('/metadata/candidates'))return [];throw Error(c.url);});await drain();const previous=p.text('batches');delay=true;
 const pending=p.elements['refresh-imports'].onclick();await drain();await p.tabs.find(t=>t.dataset.tab==='review').onclick();release([batch('late')]);await pending;assert.equal(p.text('batches'),previous);assert.match(p.text('candidates-summary'),/0 个候选/);
});
test('background import polling does not accumulate requests while a slow refresh is pending',async()=>{
 let release,slow=false;const p=page('manage',async c=>{if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/imports'))return slow?new Promise(r=>release=r):[batch()];if(c.url.endsWith('/metadata/jobs'))return [];throw Error(c.url);});await drain();slow=true;p.intervals[0]();await drain();const calls=p.calls.length;
 for(let n=0;n<10;n++)p.intervals[0]();await drain();assert.equal(p.calls.length,calls);release([batch()]);await drain();assert.match(p.text('batches'),/等待处理/);
});

test('dataset background polling is bounded and preserves focused task controls until focus leaves',async()=>{
 let slow=false,release,processed=1;
 const p=page('manage',async c=>{
  if(c.url.endsWith('/info'))return syncInfo;
  if(c.url.endsWith('/imports')||c.url.endsWith('/metadata/jobs'))return [];
  if(c.url.endsWith('/sync'))return syncStatus();
  if(c.url.endsWith('/datasets'))return slow?new Promise(r=>release=r):[{id:'synthetic-job',source:'vndb-db-2026-11-06.tar.zst',state:'queued',revision:1,processed,total:3,written:0,kept:0}];throw Error(c.url);
 });await drain();p.intervals[1]();await drain();
 const pause=p.buttons('datasets').find(b=>b.textContent==='暂停');assert.ok(pause);p.context.document.activeElement=pause;
 processed=2;p.intervals[1]();await drain();assert.equal(p.buttons('datasets')[0],pause);assert.match(p.text('datasets'),/已处理 1/);
 p.context.document.activeElement=null;p.intervals[1]();await drain();assert.match(p.text('datasets'),/已处理 2/);
 slow=true;p.intervals[1]();await drain();const calls=p.calls.length;
 for(let n=0;n<10;n++)p.intervals[1]();await drain();assert.equal(p.calls.length,calls);
 release([]);await drain();assert.match(p.text('datasets'),/没有官方导入任务/);assert.ok(p.calls.every(c=>c.method==='GET'));
});
test('activity imports continue after individual failure and retain prior results across another selection',async()=>{
 const p=page('sessions',async c=>{if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/activity/batches'))return c.body.batch_id==='bad'?{status:400,data:{code:'invalid_json',title:'synthetic invalid'}}:{batch_id:c.body.batch_id,complete:false,results:[{kind:'plays',key:'p',status:'dependency_pending'}]};if(c.url.endsWith('/plays')||c.url.endsWith('/play-sessions')||c.url.endsWith('/play-edit-conflicts'))return [];throw Error(c.url);});await drain();
 p.elements.files.files=[file('one.json',{format:'vn-library-activity',batch_id:'bad'}),file('two.json',{format:'vn-library-activity',batch_id:'good'})];await p.elements.import.onclick();assert.equal(p.calls.filter(c=>c.method==='POST').length,2);assert.match(p.text('receipts'),/one.json/);assert.match(p.text('receipts'),/two.json/);assert.match(p.text('receipts'),/依赖待补全/);
 p.elements.files.files=[file('three.json',{format:'vn-library-activity',batch_id:'third'})];await p.elements.import.onclick();assert.match(p.text('receipts'),/one.json/);assert.match(p.text('receipts'),/third|three.json/);
});
test('actual time adjustment retains exact ID, target and body when reply is lost and fields change',async()=>{
 let lose=true;const p=page('sessions',async c=>{if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/plays'))return [play];if(c.url.endsWith('/play-sessions')||c.url.endsWith('/play-edit-conflicts'))return [];if(c.url.endsWith('/time'))return time;if(c.url.endsWith('/time-adjustments')){if(lose){lose=false;throw TypeError('synthetic lost reply');}return c.body;}throw Error(c.url);});await drain();assert.match(p.text('time'),/历史未知/);
 p.elements.seconds.value='120';p.elements.reason.value='合成调整';p.elements.adjust.onsubmit({preventDefault(){}});await drain();assert.match(p.text('adjust-result'),/结果不明/);const retry=p.buttons('adjust-result').find(b=>b.textContent==='按原操作重试');assert.equal(retry.hidden,false);
 p.elements.seconds.value='999';p.elements.reason.value='后来编辑';await retry.onclick();const posts=p.calls.filter(c=>c.method==='POST');assert.equal(posts.length,2);assert.deepEqual(posts[0],posts[1]);assert.equal(posts[1].body.seconds,120);assert.equal(p.buttons('adjust-result')[0].disabled,true);
});
test('session refresh failures retain existing cards and time read errors never enable adjustments',async()=>{
 let fail=false;const session={sample:{id:'s1',play_id:'play-a',state:'ended',cumulative_seconds:123,started_at:'2026-01-01'},review_state:'pending',revision:1};
 const p=page('sessions',async c=>{if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/plays'))return fail?{status:401,data:{title:'synthetic auth'}}:[play];if(c.url.endsWith('/play-sessions'))return [session];if(c.url.endsWith('/play-edit-conflicts'))return [];if(c.url.endsWith('/time'))return {status:500,data:{title:'synthetic time failure'}};throw Error(c.url);});await drain();assert.match(p.text('sessions'),/合成作品/);assert.equal(p.elements['adjust-submit'].disabled,true);const previous=p.text('sessions');fail=true;await p.elements.refresh.onclick();assert.equal(p.text('sessions'),previous);assert.match(p.text('message'),/鉴权失败/);
});
test('candidate conflict preserves original decision, blocks contradictory writes, and requires explicit reread',async()=>{
 let revision=1,accepted=false;const candidate=()=>({id:'candidate-a',incoming:{title:'导入标题'},message:'需要比较',vndb_id:'v17',matches:[],state:'pending',expected_revision:revision});
 const p=page('manage',async c=>{
  if(c.url.endsWith('/info'))return info;if(c.url.endsWith('/metadata/candidates'))return accepted?[]:[candidate()];if(c.url.endsWith('/vns/v17'))return {items:[{title:'Web原文'}]};
  if(c.url.endsWith('/accept')){if(c.body.expected_revision===1)return {status:409,data:{title:'synthetic conflict'}};accepted=true;return {...candidate(),state:'accepted'};}throw Error(c.url);
 },'review');await drain();await p.buttons('candidates').find(b=>b.textContent==='确认采用导入资料').onclick();assert.match(p.text('candidates'),/冲突/);
 await p.buttons('candidates').find(b=>b.textContent==='保留现有资料／拒绝候选').onclick();assert.equal(p.calls.filter(c=>c.method==='POST').length,1);
 revision=2;await p.elements['refresh-candidates'].onclick();assert.match(p.text('tool-history'),/expected_revision/);
 await p.buttons('candidates').find(b=>b.textContent==='确认采用导入资料').onclick();const posts=p.calls.filter(c=>c.method==='POST');assert.equal(posts.length,2);assert.notEqual(posts[0].key,posts[1].key);assert.equal(posts[1].body.expected_revision,2);assert.match(p.text('candidates'),/没有待确认/);
});
test('restore uses exact item identity and revision and reports acknowledged write separately from failed refresh',async()=>{
 let fail=false;const item={id:'item-original',details:{title:'合成移除项'},revision:7};
 const p=page('manage',async c=>{
  if(c.url.endsWith('/info'))return info;if(c.url.includes('/items?deleted'))return fail?{status:500,data:{title:'synthetic refresh failed'}}:{items:[{item}],total:1};if(c.url.includes('/plays?deleted'))return [];
  if(c.url.endsWith('/item-original/restore')){fail=true;return {...item,revision:8};}throw Error(c.url);
 },'trash');await drain();await p.buttons('removed').find(b=>b.textContent==='恢复书架条目').onclick();const post=p.calls.find(c=>c.method==='POST');assert.equal(post.url,'/api/v1/items/item-original/restore');assert.deepEqual(post.body,{expected_revision:7});assert.match(p.text('removed'),/Web已确认/);assert.match(p.text('message'),/保留最近成功/);assert.equal(p.buttons('removed')[0].disabled,true);
});
test('quality late read failure cannot replace a newer successful filter summary',async()=>{
 let delayed=false,release;const p=page('quality',async c=>{
  if(c.url.endsWith('/devices'))return [];if(c.url.includes('/quality?')){if(delayed)return new Promise(r=>release=r);return {counts:{company_missing:2},items:[],more:false,total:2};}throw Error(c.url);
 });await drain();delayed=true;const old=p.elements.filters.onsubmit({preventDefault(){}});await drain();delayed=false;p.elements.filters.onsubmit({preventDefault(){}});await drain();release({status:500,data:{title:'late failure'}});await old;await drain();assert.match(p.text('summary'),/会社待补全 2/);assert.doesNotMatch(p.text('notice'),/late failure/);
});


test('actual personal organization controls populate stable labels and filter before paging',async()=>{
 const item={id:'exact-a',details:{title:'Synthetic A',novel_id:'v17'},revision:1};
 const state={revision:2,labels:[{id:'label-a',name:'合成想玩',kind:'collection'}],items:[{item_id:'exact-a',favorite:true,label_ids:['label-a']}]};
 const p=page('manage',async c=>{
  if(c.url==='/api/v1/info')return {...info,service_instance_id:'synthetic-instance',capabilities:['personal_organization']};
  if(c.url==='/api/v1/personal-organization')return state;
  if(c.url.includes('/shelf?'))return {format:'vn-shelf-summary',format_version:1,items:[{item,play_count:0,state:{progress:{state:'unrecorded',statuses:[]},is_local:false}}],total:1,more:false};
  throw Error(c.url);
 },'shelf',true);await drain();
 assert.match(p.text('personal-label-filter'),/合成想玩/);assert.match(p.text('items'),/★ 收藏/);
 p.elements['personal-label-filter'].value='label-a';p.elements['personal-label-filter'].onchange();await drain();
 p.elements['favorite-filter'].value='true';p.elements['favorite-filter'].onchange();await drain();
 const query=new URL(p.calls.at(-1).url,'https://example.test').searchParams;
 assert.equal(query.get('favorite'),'true');assert.equal(query.get('label'),'label-a');assert.equal(query.get('page'),'1');assert.ok(p.calls.every(c=>c.method==='GET'));
 p.elements['clear-filters'].onclick();await drain();assert.equal(new URL(p.calls.at(-1).url,'https://example.test').searchParams.get('favorite'),null);
});
