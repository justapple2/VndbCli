// Run the actual page scripts against synthetic DOM/API adapters, with no browser or network.
const {test}=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs'),path=require('node:path');
const root=path.join(__dirname,'../../src/Vndb.Web/wwwroot');
class Element {
 constructor(tag='div'){this.tagName=tag.toUpperCase();this.children=[];this.dataset={};this.hidden=false;this.disabled=false;this._text='';this._value=undefined;this.classList={toggle(){},add(){},remove(){}};}
 append(...nodes){for(const n of nodes){if(n.tagName==='#FRAGMENT'){this.append(...n.children);continue;}n.parentNode=this;this.children.push(n);}}
 prepend(...nodes){this.children.unshift(...nodes);}
 replaceChildren(...nodes){this.children=[];this._text='';this.append(...nodes);}
 set textContent(v){this._text=String(v??'');this.children=[];} get textContent(){return this._text+this.children.map(n=>n.textContent).join(' ');}
 set value(v){this._value=String(v??'');}get value(){return this._value??this.children.find(n=>n.tagName==='OPTION')?.value??'';}
 setAttribute(k,v){this[k]=v;}addEventListener(){} showModal(){this.open=true;}close(){this.open=false;}
 add(option){this.append(option);}
 querySelectorAll(selector){const match=n=>selector==='button'?n.tagName==='BUTTON':selector==='details'?n.tagName==='DETAILS':selector.startsWith('.')?(n.className||'').split(' ').includes(selector.slice(1)):false;return this.children.flatMap(n=>[...(match(n)?[n]:[]),...n.querySelectorAll(selector)]);}
 querySelector(s){return this.querySelectorAll(s)[0]||null;}
}
const drain=async()=>{for(let i=0;i<12;i++)await new Promise(resolve=>setImmediate(resolve));};
function page(name,route,tab='imports'){
 const html=fs.readFileSync(path.join(root,name+'.html'),'utf8'),elements={};for(const m of html.matchAll(/<(\w+)[^>]*\bid="([^"]+)"/g))elements[m[2]]=new Element(m[1]);
 const tabs=['shelf','imports','review','trash'].map(value=>{const e=new Element('button');e.dataset.tab=value;return e;}),calls=[],intervals=[];
 const document={getElementById:id=>{assert.ok(elements[id],`Actual ${name}.html must declare ${id}`);return elements[id];},createElement:tag=>new Element(tag),createDocumentFragment:()=>new Element('#fragment'),querySelectorAll:s=>s==='[data-tab]'?tabs:[],visibilityState:'visible'};
 const context={document,URLSearchParams,URL,AbortSignal,crypto:require('node:crypto').webcrypto,structuredClone,console,location:{search:'?tab='+tab},scrollY:0,setTimeout,clearTimeout,setInterval:fn=>{intervals.push(fn);return 1;},sessionStorage:{getItem:()=>'',setItem(){}},addEventListener(){},VnShell:{setView(){}},
  fetch:async(url,options={})=>{const call={url,method:options.method||'GET',body:options.body?JSON.parse(options.body):undefined,key:options.headers?.['Idempotency-Key']};calls.push(call);const r=await route(call);return {ok:!r.status||r.status<400,status:r.status||200,json:async()=>r.data??r};}};
 context.Option=function(text,value){const e=new Element('option');e.textContent=text;e.value=value;return e;};context.window=context;context.VnPersonal={create:()=>({open(){}})};context.VnView={element:(tag,cls,text)=>{const e=new Element(tag);e.className=cls;e.textContent=text;return e;},storage:()=>({get:()=>null,set(){}}),store:(_id,defaults)=>({read:()=>({...defaults}),save(){}}),latest:()=>({cancel(){},begin(){return {current:()=>true};}}),appearance(){},select(){},failure:e=>e.message,restoreScroll(){}};
 vm.createContext(context);
 const adapter=context.VnView;vm.runInContext(fs.readFileSync(path.join(root,'library-view.js'),'utf8'),context,{filename:'library-view.js'});
 context.VnView={...adapter,card:context.VnView.card,itemBadges:context.VnView.itemBadges,progressLabel:context.VnView.progressLabel};
 for(const file of ['tool-feedback.js','import-input.js',name+'.js'])vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context,{filename:file});
 return {elements,tabs,calls,intervals,context,buttons:id=>elements[id].querySelectorAll('button'),text:id=>elements[id].textContent};
}
const info={import_limits:{maximum_bytes:1048576,maximum_items:500}};
const batch=(id='import-original')=>({id,state:'queued',accepted:true,total:1,completed:0,items:[{index:0,title:'合成タイトル',status:'preview'}]});
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
